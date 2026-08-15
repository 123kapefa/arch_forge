using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class ServiceCycleDetectorTests
{
    private static SystemNode Svc(string id) => new(id, id, SystemNodeKind.Service, id + ":1");
    private static SystemEdge Http(string a, string b, bool deferred = false) =>
        new(a, b, "http-call", EdgeVia.Config, deferred ? 0.4 : 0.8, deferred);

    [Fact]
    public void Fires_on_two_service_cycle()
    {
        var g = new SystemGraph([Svc("a"), Svc("b")], [Http("a", "b"), Http("b", "a")], []);

        Assert.Contains(ServiceCycleDetector.Detect(g),
            p => p is { Kind: "service-cycle", Severity: ProblemSeverity.Critical });
    }

    [Fact]
    public void No_cycle_in_dag()
    {
        var g = new SystemGraph([Svc("a"), Svc("b"), Svc("c")],
            [Http("a", "b"), Http("b", "c"), Http("a", "c")], []);

        Assert.Empty(ServiceCycleDetector.Detect(g));
    }

    [Fact]
    public void Three_service_cycle_reported_once()
    {
        var g = new SystemGraph([Svc("a"), Svc("b"), Svc("c")],
            [Http("a", "b"), Http("b", "c"), Http("c", "a")], []);

        var p = ServiceCycleDetector.Detect(g);
        Assert.Single(p);
        Assert.Contains("a", p[0].Title);
    }

    [Fact]
    public void Ignores_rwdb_and_deferred_edges()
    {
        // цикл только по УВЕРЕННЫМ http-call между своими сервисами; rw-db и deferred не считаем
        var g = new SystemGraph([Svc("a"), Svc("b")],
            [new SystemEdge("a", "b", "rw-db", EdgeVia.Config, 0.9), Http("b", "a", deferred: true)], []);

        Assert.Empty(ServiceCycleDetector.Detect(g));
    }
}
