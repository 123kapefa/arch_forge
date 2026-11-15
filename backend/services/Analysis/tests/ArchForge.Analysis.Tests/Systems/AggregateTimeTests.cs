using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Правило aggregate-time: детектор над фактами IAggregateTimeSource, привязка к узлу, линза, профиль, каталог.
public class AggregateTimeTests
{
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_time_det_" + Guid.NewGuid().ToString("N"))).FullName;

    private static TimeSite S(string type, string member, string name, int line = 10, string layer = "Domain") => new(type, member, name, "/p/A.cs", line, layer);

    private static ServiceAnalysis Sa(string name, string location, string? nodeId, TimeSite[] clock, TimeSite[] audit) =>
        new(new DiscoveredService(name, "/p/" + name, "docker-compose.yml"), location, Result: null)
        { NodeId = nodeId, AggregateTime = new AggregateTimeFacts(clock, audit) };

    [Fact]
    public void Clock_and_audit_sites_become_one_info_problem_ordered_by_file_and_line()
    {
        var p = Assert.Single(AggregateTimeDetector.Detect([Sa("tags", "tags", null,
            [S("Tag", "Rename", "DateTime.UtcNow", 30), S("Tag", ".ctor", "DateTime.UtcNow", 5)],
            [S("AuditInterceptor", "Saving", "UpdatedAt", 12, "Infrastructure")])]));
        Assert.Equal(AggregateTimeDetector.Kind, p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Equal(ProblemCategory.Convention, p.Category);
        Assert.Contains("2 мест читают часы", p.Title);
        Assert.Contains("1 аудит-полей", p.Title);
        Assert.Equal([5, 12, 30], p.Items!.Select(i => i.Line!.Value).ToArray());
        Assert.Contains("TimeProvider", p.Items![0].Note);
        Assert.Contains("interceptor", p.Items![1].Note);
    }

    [Fact]
    public void Nothing_or_only_tests_is_no_problem_monorepo_node_is_used_and_items_are_capped()
    {
        Assert.Empty(AggregateTimeDetector.Detect([Sa("a", "a", null, [], []), Sa("b", "b", null, [S("T", "M", "DateTime.UtcNow", layer: "tests")], [])]));
        var p1 = Assert.Single(AggregateTimeDetector.Detect([Sa("x", "x", "edu/x", [S("A", "M", "DateTime.Now")], [])]));
        Assert.Equal("edu/x", p1.Location);
        var many = Enumerable.Range(0, AggregateTimeDetector.MaxItems + 30).Select(i => S("A", "M" + i, "DateTime.UtcNow", i)).ToArray();
        var p2 = Assert.Single(AggregateTimeDetector.Detect([Sa("big", "big", null, many, [])]));
        Assert.Equal(AggregateTimeDetector.MaxItems, p2.Items!.Count);
        Assert.Contains("показано", p2.Title);
    }

    [Fact]
    public void Catalog_has_the_rule_soft_convention_off_on_simple()
    {
        var rule = RuleCatalog.Find("aggregate-time")!;
        Assert.Equal(ProblemCategory.Convention, rule.Category);
        Assert.NotNull(rule.Example);
        Assert.Equal(Strictness.Off, rule.DefaultFor(ProjectLevel.Simple));
    }

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
    private sealed class FakeTime : IAggregateTimeSource
    {
        public int Reads;
        public AggregateTimeFacts Read(string root, CancellationToken ct = default) { Reads++; return new([S("Tag", "Rename", "DateTime.UtcNow")], []); }
    }
    private sealed class ThrowingTime : IAggregateTimeSource
    {
        public AggregateTimeFacts Read(string root, CancellationToken ct = default) => throw new IOException("boom");
    }

    [Fact]
    public void Handler_reports_it_only_with_drift_lens_profile_controls_it_and_reading_is_lazy()
    {
        var src = new FakeTime();
        var h = new GetSystemHandler(
            new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
            new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), aggregateTime: src));
        var r = h.Handle(new GetSystemQuery(Root, Drift: true))!;
        var p = Assert.Single(r.Problems!, x => x.Kind == AggregateTimeDetector.Kind);
        Assert.Equal("convention", p.Category);
        Assert.Contains("TimeProvider", p.Fix);
        Assert.Equal(1, src.Reads);

        h.Handle(new GetSystemQuery(Root, Layers: true));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(ProjectLevel.Simple)));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["aggregate-time"] = Strictness.Off })));
        Assert.Equal(1, src.Reads);
    }

    [Fact]
    public void Failing_source_does_not_break_the_service()
    {
        var sa = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), aggregateTime: new ThrowingTime()).Analyze(Root, ConventionPack.Default()));
        Assert.NotNull(sa.Result);
        Assert.Empty(sa.AggregateTime.DomainClock);
    }
}
