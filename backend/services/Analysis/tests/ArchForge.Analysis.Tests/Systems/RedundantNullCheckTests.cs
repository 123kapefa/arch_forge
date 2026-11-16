using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Правило redundant-null-check: детектор над проверками из IGuardSource, привязка к узлу, флаг линзы, профиль, каталог.
public class RedundantNullCheckTests
{
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_guard_det_" + Guid.NewGuid().ToString("N"))).FullName;

    private static NullGuardSite Site(string type, string member, string param, string form = "ThrowIfNull", string layer = "Application", int line = 10, string file = "/p/A.cs") =>
        new(type, member, param, form, file, line, layer);

    private static ServiceAnalysis Sa(string name, string location, string? nodeId, params NullGuardSite[] guards) =>
        new(new DiscoveredService(name, "/p/" + name, "docker-compose.yml"), location, Result: null) { NodeId = nodeId, Guards = guards };

    // ── детектор ──

    [Fact]
    public void Groups_sites_per_service_into_one_info_problem_with_items()
    {
        var p = Assert.Single(RedundantNullCheckDetector.Detect([Sa("orders", "orders", null,
            Site("Handler", "Rename", "name", line: 12), Site("Handler", ".ctor", "foo", "if-throw", line: 5))]));

        Assert.Equal(RedundantNullCheckDetector.Kind, p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Equal(ProblemCategory.Convention, p.Category);
        Assert.Equal("orders", p.Location);
        Assert.Contains("2 лишних", p.Title);
        Assert.Equal(["Handler..ctor(foo)", "Handler.Rename(name)"], p.Items!.Select(i => i.Name).ToArray());   // порядок: файл, строка
        Assert.All(p.Items!, i => Assert.Equal("/p/A.cs", i.File));
        Assert.Contains("ThrowIfNull", p.Items!.Single(i => i.Name.EndsWith("Rename(name)")).Note);
    }

    [Fact]
    public void Problem_points_at_bound_node_in_monorepo_and_tests_layer_is_ignored()
    {
        var p = Assert.Single(RedundantNullCheckDetector.Detect([Sa("CommentService", "edu", "edu/comment-service",
            Site("H", "M", "a"), Site("T", "M", "b", layer: "tests"))]));
        Assert.Equal("edu/comment-service", p.Location);
        Assert.Single(p.Items!);
        Assert.Contains("1 лишних", p.Title);
    }

    [Fact]
    public void No_sites_or_only_tests_means_no_problem_and_items_are_capped()
    {
        Assert.Empty(RedundantNullCheckDetector.Detect([Sa("a", "a", null), Sa("b", "b", null, Site("T", "M", "x", layer: "tests"))]));
        var many = Enumerable.Range(0, RedundantNullCheckDetector.MaxItems + 50).Select(i => Site("H", "M" + i, "p", line: i)).ToArray();
        var p = Assert.Single(RedundantNullCheckDetector.Detect([Sa("big", "big", null, many)]));
        Assert.Equal(RedundantNullCheckDetector.MaxItems, p.Items!.Count);
        Assert.Contains("показано", p.Title);
    }

    // ── каталог ──

    [Fact]
    public void Catalog_has_the_rule_with_example_and_it_is_soft_convention_off_on_simple()
    {
        var rule = RuleCatalog.Find("redundant-null-check")!;
        Assert.Equal(ProblemCategory.Convention, rule.Category);
        Assert.NotNull(rule.Example);
        Assert.Contains(rule.Example!.Bad, l => l.Contains("ThrowIfNull"));
        Assert.DoesNotContain(rule.Example.Good, l => l.Contains("ThrowIfNull"));
        Assert.Equal(Strictness.Off, rule.DefaultFor(ProjectLevel.Simple));
        Assert.Null(rule.DefaultFor(ProjectLevel.Standard));       // своя серьёзность детектора (Info)
    }

    // ── сквозь обработчик системы ──

    private sealed class FakeSource(SystemGraph g) : ISystemSource
    {
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default) => g;
    }
    private sealed class FakeDiscovery : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => [new("orders", Path.Combine(root, "orders"), "docker-compose.yml")];
    }
    private sealed class FakeTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => [];
    }
    private sealed class FakeGuards : IGuardSource
    {
        public IReadOnlyList<NullGuardSite> Read(string root, CancellationToken ct = default) => [Site("Handler", "Rename", "name")];
    }

    private static GetSystemHandler Handler() => new(
        new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
        new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), guards: new FakeGuards()));

    [Fact]
    public void Handler_reports_it_with_the_drift_lens_only_and_profile_can_turn_it_off()
    {
        var withDrift = Handler().Handle(new GetSystemQuery(Root, Drift: true))!;
        Assert.NotNull(withDrift.Problems);
        var p = Assert.Single(withDrift.Problems, x => x.Kind == RedundantNullCheckDetector.Kind);
        Assert.Equal("convention", p.Category);
        Assert.Single(p.Items!);
        Assert.Contains("Nullable", p.Fix);                       // подсказка «как чинить»

        Assert.DoesNotContain(Handler().Handle(new GetSystemQuery(Root, Layers: true))!.Problems ?? [], x => x.Kind == RedundantNullCheckDetector.Kind);   // без линзы свода — нет

        var off = new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["redundant-null-check"] = Strictness.Off });
        Assert.DoesNotContain(Handler().Handle(new GetSystemQuery(Root, Drift: true, Profile: off))!.Problems ?? [], x => x.Kind == RedundantNullCheckDetector.Kind);

        var simple = new ProjectProfile(ProjectLevel.Simple);
        Assert.DoesNotContain(Handler().Handle(new GetSystemQuery(Root, Drift: true, Profile: simple))!.Problems ?? [], x => x.Kind == RedundantNullCheckDetector.Kind);
    }

    [Fact]
    public void Failing_guard_source_does_not_break_the_service()
    {
        var failing = new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), guards: new ThrowingGuards());
        var sa = Assert.Single(failing.Analyze(Root, ConventionPack.Default()));
        Assert.NotNull(sa.Result);
        Assert.Empty(sa.Guards);
    }

    private sealed class ThrowingGuards : IGuardSource
    {
        public IReadOnlyList<NullGuardSite> Read(string root, CancellationToken ct = default) => throw new IOException("boom");
    }

    [Fact]
    public void Guards_are_not_read_when_the_convention_lens_is_off_or_the_rule_is_disabled()
    {
        var counting = new CountingGuards();
        var h = new GetSystemHandler(
            new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
            new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), guards: counting));

        h.Handle(new GetSystemQuery(Root, Layers: true));
        Assert.Equal(0, counting.Reads);                                            // без линзы свода код на null не читаем
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(ProjectLevel.Simple)));
        Assert.Equal(0, counting.Reads);                                            // на Simple правило выключено
        h.Handle(new GetSystemQuery(Root, Drift: true));
        Assert.Equal(1, counting.Reads);
    }

    private sealed class CountingGuards : IGuardSource
    {
        public int Reads { get; private set; }
        public IReadOnlyList<NullGuardSite> Read(string root, CancellationToken ct = default) { Reads++; return []; }
    }
}
