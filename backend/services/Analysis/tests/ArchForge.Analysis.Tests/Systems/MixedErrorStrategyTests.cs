using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Правило mixed-error-strategy: детектор над фактами IErrorStrategySource, меньшинство в списке, привязка к узлу, линза, профиль, каталог.
public class MixedErrorStrategyTests
{
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_err_det_" + Guid.NewGuid().ToString("N"))).FullName;

    private static ErrorSite S(string type, string member, string name, int line = 10, string layer = "Application") => new(type, member, name, "/p/A.cs", line, layer);

    private static ServiceAnalysis Sa(string name, string location, string? nodeId, ErrorSite[] results, ErrorSite[] throws) =>
        new(new DiscoveredService(name, "/p/" + name, "docker-compose.yml"), location, Result: null)
        { NodeId = nodeId, ErrorStrategy = new ErrorStrategyFacts(results, throws) };

    [Fact]
    public void Only_one_strategy_or_none_is_not_a_mix()
    {
        Assert.Empty(MixedErrorStrategyDetector.Detect([
            Sa("a", "a", null, [], []),
            Sa("b", "b", null, [S("H", "Get", "Result")], []),
            Sa("c", "c", null, [], [S("H", "Get", "NotFoundException")]),
            Sa("d", "d", null, [S("T", "A", "Result", layer: "tests")], [S("T", "B", "NotFoundException")])]));
    }

    [Fact]
    public void Mix_reports_the_minority_side_as_one_info_problem()
    {
        var p = Assert.Single(MixedErrorStrategyDetector.Detect([Sa("orders", "orders", null,
            [S("H", "Get", "Result<…>", 1), S("H", "List", "Result<…>", 2), S("H", "Cancel", "Result", 3)],
            [S("H", "Find", "NotFoundException", 7)])]));
        Assert.Equal(MixedErrorStrategyDetector.Kind, p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Equal(ProblemCategory.Convention, p.Category);
        Assert.Contains("3 методов возвращают Result", p.Title);
        Assert.Contains("1 методов бросают", p.Title);
        var item = Assert.Single(p.Items!);                                         // преобладает Result → в списке бросок
        Assert.Equal("H.Find", item.Name);
        Assert.Contains("NotFoundException", item.Note);

        var q = Assert.Single(MixedErrorStrategyDetector.Detect([Sa("x", "x", "edu/x", [S("H", "Get", "Result")], [S("H", "A", "NotFoundException"), S("H", "B", "ConflictException")])]));
        Assert.Equal("edu/x", q.Location);                                          // монорепо: узел сервиса
        Assert.Equal(["H.Get"], q.Items!.Select(i => i.Name).ToArray());           // преобладают броски → в списке Result
    }

    [Fact]
    public void Catalog_has_the_rule_soft_convention_off_on_simple()
    {
        var rule = RuleCatalog.Find("mixed-error-strategy")!;
        Assert.Equal(ProblemCategory.Convention, rule.Category);
        Assert.NotNull(rule.Example);
        Assert.Equal(Strictness.Off, rule.DefaultFor(ProjectLevel.Simple));
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
    private sealed class FakeErrors : IErrorStrategySource
    {
        public int Reads;
        public ErrorStrategyFacts Read(string root, CancellationToken ct = default) { Reads++; return new([S("H", "Get", "Result")], [S("H", "Find", "NotFoundException")]); }
    }
    private sealed class ThrowingErrors : IErrorStrategySource
    {
        public ErrorStrategyFacts Read(string root, CancellationToken ct = default) => throw new IOException("boom");
    }

    private static GetSystemHandler Handler(IErrorStrategySource errors) => new(
        new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
        new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), errors: errors));

    [Fact]
    public void Handler_reports_it_only_with_drift_lens_and_profile_controls_it_and_reading_is_lazy()
    {
        var errs = new FakeErrors();
        var h = Handler(errs);
        var r = h.Handle(new GetSystemQuery(Root, Drift: true))!;
        var p = Assert.Single(r.Problems!, x => x.Kind == MixedErrorStrategyDetector.Kind);
        Assert.Equal("convention", p.Category);
        Assert.Contains("Result", p.Fix);
        Assert.Equal(1, errs.Reads);

        h.Handle(new GetSystemQuery(Root, Layers: true));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(ProjectLevel.Simple)));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["mixed-error-strategy"] = Strictness.Off })));
        Assert.Equal(1, errs.Reads);                                                // без линзы / на Simple / выключено — код не читаем
    }

    [Fact]
    public void Failing_source_does_not_break_the_service()
    {
        var sa = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), errors: new ThrowingErrors()).Analyze(Root, ConventionPack.Default()));
        Assert.NotNull(sa.Result);
        Assert.Empty(sa.ErrorStrategy.ExpectedThrows);
    }
}
