using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class DanglingEventDetectorTests
{
    private static SystemNode Svc(string id) => new(id, id, SystemNodeKind.Service, id + ":1");
    private static SystemNode Bus(string id) => new(id, id, SystemNodeKind.Topic, "rabbitmq:3");
    private static SystemEdge Edge(string src, string dst, string kind, bool deferred = false) =>
        new(src, dst, kind, EdgeVia.Code, deferred ? 0.4 : 0.8, deferred);

    [Fact]
    public void Publish_without_subscribe_is_dangling_event()
    {
        var g = new SystemGraph([Svc("p"), Bus("bus")], [Edge("p", "bus", "publishes")], []);

        Assert.Contains(DanglingEventDetector.Detect(g),
            x => x is { Kind: "dangling-event", Severity: ProblemSeverity.Warning });
    }

    [Fact]
    public void Subscribe_without_publish_is_orphaned_subscription()
    {
        var g = new SystemGraph([Svc("c"), Bus("bus")], [Edge("c", "bus", "subscribes")], []);

        Assert.Contains(DanglingEventDetector.Detect(g),
            x => x is { Kind: "orphaned-subscription", Severity: ProblemSeverity.Warning });
    }

    [Fact]
    public void Publish_and_subscribe_is_healthy_flow()
    {
        var g = new SystemGraph([Svc("p"), Svc("c"), Bus("bus")],
            [Edge("p", "bus", "publishes"), Edge("c", "bus", "subscribes")], []);

        Assert.Empty(DanglingEventDetector.Detect(g));
    }

    [Fact]
    public void Uses_bus_only_is_silent_direction_unknown()
    {
        // compose даёт лишь «подключается к шине» без направления → НЕ выдумываем dangling (adr/0008)
        var g = new SystemGraph([Svc("a"), Svc("b"), Bus("bus")],
            [Edge("a", "bus", "uses-bus"), Edge("b", "bus", "uses-bus")], []);

        Assert.Empty(DanglingEventDetector.Detect(g));
    }

    [Fact]
    public void Topic_without_edges_is_isolated_coverage()
    {
        var g = new SystemGraph([Bus("bus")], [], []);

        Assert.Contains(DanglingEventDetector.Detect(g),
            x => x is { Kind: "isolated-topic", Severity: ProblemSeverity.Coverage });
    }

    [Fact]
    public void Deferred_publish_does_not_count()
    {
        // отложенное (unknown ${VAR}) publish-ребро не считаем достоверным издателем
        var g = new SystemGraph([Svc("p"), Bus("bus")],
            [Edge("p", "bus", "publishes", deferred: true)], []);

        // единственное ребро — deferred → как «только uses-bus»: не dangling; но топик не без рёбер → не isolated
        Assert.Empty(DanglingEventDetector.Detect(g));
    }
}
