using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Правило aggregate-events: детектор над фактами IAggregateEventSource, привязка к узлу, линза, профиль, каталог.
public class AggregateEventTests
{
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_ev_det_" + Guid.NewGuid().ToString("N"))).FullName;

    private static EventSite S(string type, string member, EventGap gap = EventGap.NoEvent, int line = 10, string layer = "Domain") => new(type, member, gap, "UpdatedAt", "/p/A.cs", line, layer);

    private static ServiceAnalysis Sa(string name, string location, string? nodeId, params EventSite[] sites) =>
        new(new DiscoveredService(name, "/p/" + name, "docker-compose.yml"), location, Result: null)
        { NodeId = nodeId, AggregateEvents = new AggregateEventFacts(sites) };

    [Fact]
    public void Sites_become_one_info_problem_ordered_by_line()
    {
        var p = Assert.Single(AggregateEventDetector.Detect([Sa("tags", "tags", null, S("Tag", "Rename", EventGap.NoEvent, 30), S("Tag", "Merge", EventGap.NoEvent | EventGap.NoTimestamp, 5), S("Tag", "Hide", EventGap.NoTimestamp, 12))]));
        Assert.Equal(AggregateEventDetector.Kind, p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Equal(ProblemCategory.Convention, p.Category);
        Assert.Contains("2 мутаций без доменного события", p.Title);
        Assert.Contains("2 без обновления времени", p.Title);
        Assert.Equal([5, 12, 30], p.Items!.Select(i => i.Line!.Value).ToArray());
        Assert.Contains("RaiseDomainEvent", p.Items![0].Note);
        Assert.Contains("UpdatedAt", p.Items![1].Note);
    }

    [Fact]
    public void Nothing_or_only_tests_is_no_problem_monorepo_node_is_used_and_items_are_capped()
    {
        Assert.Empty(AggregateEventDetector.Detect([Sa("a", "a", null), Sa("b", "b", null, S("T", "M", layer: "tests"))]));
        var p1 = Assert.Single(AggregateEventDetector.Detect([Sa("x", "x", "edu/x", S("A", "M"))]));
        Assert.Equal("edu/x", p1.Location);
        var many = Enumerable.Range(0, AggregateEventDetector.MaxItems + 30).Select(i => S("A", "M" + i, line: i)).ToArray();
        var p2 = Assert.Single(AggregateEventDetector.Detect([Sa("big", "big", null, many)]));
        Assert.Equal(AggregateEventDetector.MaxItems, p2.Items!.Count);
        Assert.Contains("показано", p2.Title);
    }

    [Fact]
    public void Catalog_has_the_rule_soft_convention_off_on_simple()
    {
        var rule = RuleCatalog.Find("aggregate-events")!;
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
    private sealed class FakeEvents : IAggregateEventSource
    {
        public int Reads;
        public AggregateEventFacts Read(string root, CancellationToken ct = default) { Reads++; return new([S("Tag", "Rename")]); }
    }
    private sealed class ThrowingEvents : IAggregateEventSource
    {
        public AggregateEventFacts Read(string root, CancellationToken ct = default) => throw new IOException("boom");
    }

    [Fact]
    public void Handler_reports_it_only_with_drift_lens_profile_controls_it_and_reading_is_lazy()
    {
        var src = new FakeEvents();
        var h = new GetSystemHandler(
            new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
            new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), aggregateEvents: src));
        var r = h.Handle(new GetSystemQuery(Root, Drift: true))!;
        var p = Assert.Single(r.Problems!, x => x.Kind == AggregateEventDetector.Kind);
        Assert.Equal("convention", p.Category);
        Assert.Contains("RaiseDomainEvent", p.Fix);
        Assert.Equal(1, src.Reads);

        h.Handle(new GetSystemQuery(Root, Layers: true));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(ProjectLevel.Simple)));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["aggregate-events"] = Strictness.Off })));
        Assert.Equal(1, src.Reads);
    }

    [Fact]
    public void Failing_source_does_not_break_the_service()
    {
        var sa = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), aggregateEvents: new ThrowingEvents()).Analyze(Root, ConventionPack.Default()));
        Assert.NotNull(sa.Result);
        Assert.Empty(sa.AggregateEvents.Sites);
    }
}
