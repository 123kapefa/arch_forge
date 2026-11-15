using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Направление pub/sub из кода → рёбра publishes/subscribes и dangling-event / orphaned-subscription (#61).
public class PubSubGraphBuilderTests
{
    private static SystemNode Svc(string id) => new(id, id, SystemNodeKind.Service, id + ":1");
    private static readonly SystemNode Bus = new("sys/rabbit", "rabbit", SystemNodeKind.Topic, "rabbitmq:3");
    private static SystemEdge UsesBus(string svc) => new($"sys/{svc}", Bus.Id, "uses-bus", EdgeVia.Config, 0.8);
    private static BusSite Pub(string name = "OrderCreated", double c = 0.8, int line = 3) => new(BusDirection.Publish, name, "/r/P.cs", line, BusBasis.Marker, c);
    private static BusSite Sub(string name = "OrderCreated", double c = 0.9, int line = 5) => new(BusDirection.Subscribe, name, "/r/C.cs", line, BusBasis.Marker, c);

    private static ServiceAnalysis Analysis(string svc, params BusSite[] sites) =>
        new(new DiscoveredService(svc, $"/r/{svc}", $"/r/{svc}/docker-compose.yml"), "sys", new AnalysisResult([], [], new Coverage(0, 0, 0), []))
        { NodeId = $"sys/{svc}", BusSites = sites };

    private static SystemGraph Graph(params string[] services) =>
        new([.. services.Select(s => Svc($"sys/{s}")), Bus], [.. services.Select(UsesBus)], []);

    [Fact]
    public void Publisher_without_subscriber_gives_dangling_event_with_source_and_confidence()
    {
        var g = PubSubGraphBuilder.Enrich(Graph("orders", "mail"), [Analysis("orders", Pub()), Analysis("mail", Pub("Other", 0.8, 9))]);

        Assert.Contains(g.Edges, e => e is { Kind: "publishes", Source: "sys/orders", Target: "sys/rabbit", Via: EdgeVia.Code });
        var p = Assert.Single(g.Problems, x => x.Kind == "dangling-event");
        Assert.Equal(ProblemSeverity.Warning, p.Severity);
        Assert.Contains(p.Items!, i => i is { Name: "OrderCreated", File: "/r/P.cs", Line: 3 } && i.Note.Contains("0.80", StringComparison.Ordinal));
    }

    [Fact]
    public void Subscriber_without_publisher_gives_orphaned_subscription()
    {
        var g = PubSubGraphBuilder.Enrich(Graph("orders", "mail"), [Analysis("orders", Sub()), Analysis("mail", Sub("X"))]);

        Assert.Contains(g.Edges, e => e is { Kind: "subscribes", Source: "sys/orders" });
        Assert.Contains(g.Problems, x => x.Kind == "orphaned-subscription" && x.Severity == ProblemSeverity.Warning);
        Assert.DoesNotContain(g.Problems, x => x.Kind == "dangling-event");
    }

    [Fact]
    public void Publisher_and_subscriber_is_healthy()
    {
        var g = PubSubGraphBuilder.Enrich(Graph("orders", "mail"), [Analysis("orders", Pub()), Analysis("mail", Sub())]);

        Assert.Equal(2, g.Edges.Count(e => e.Kind is "publishes" or "subscribes"));
        Assert.DoesNotContain(g.Problems, x => x.Kind is "dangling-event" or "orphaned-subscription");
    }

    [Fact]
    public void Low_confidence_evidence_downgrades_to_coverage()
    {
        var g = PubSubGraphBuilder.Enrich(Graph("orders"), [Analysis("orders", Pub("", 0.5))]);

        var p = Assert.Single(g.Problems, x => x.Kind == "dangling-event");
        Assert.Equal(ProblemSeverity.Coverage, p.Severity);
    }

    [Fact]
    public void Unknown_direction_in_any_service_of_the_bus_keeps_silence()
    {
        var unknown = new BusSite(BusDirection.Unknown, "", "/r/R.cs", 1, BusBasis.Unresolved, 0);
        var g = PubSubGraphBuilder.Enrich(Graph("orders", "mail"), [Analysis("orders", Pub()), Analysis("mail", unknown)]);

        Assert.DoesNotContain(g.Edges, e => e.Kind is "publishes" or "subscribes");
        Assert.DoesNotContain(g.Problems, x => x.Kind is "dangling-event" or "orphaned-subscription");
    }

    [Fact]
    public void Service_on_the_bus_without_any_code_site_keeps_silence()
    {
        var g = PubSubGraphBuilder.Enrich(Graph("orders", "mail"), [Analysis("orders", Pub()), Analysis("mail")]);

        Assert.DoesNotContain(g.Problems, x => x.Kind == "dangling-event");
    }

    [Fact]
    public void Service_on_the_bus_that_was_not_analyzed_keeps_silence()
    {
        var g = PubSubGraphBuilder.Enrich(Graph("orders", "mail"), [Analysis("orders", Pub())]);

        Assert.DoesNotContain(g.Problems, x => x.Kind == "dangling-event");
    }

    [Fact]
    public void Service_on_two_buses_gets_no_direction_edges()
    {
        var bus2 = new SystemNode("sys/kafka", "kafka", SystemNodeKind.Topic, "kafka:3");
        var g0 = Graph("orders");
        var g = PubSubGraphBuilder.Enrich(
            g0 with { Nodes = [.. g0.Nodes, bus2], Edges = [.. g0.Edges, new SystemEdge("sys/orders", bus2.Id, "uses-bus", EdgeVia.Config, 0.8)] },
            [Analysis("orders", Pub())]);

        Assert.DoesNotContain(g.Edges, e => e.Kind is "publishes" or "subscribes");
    }

    [Fact]
    public void Unresolved_publish_does_not_hide_orphaned_but_names_are_not_matched()
    {
        // направление (шина-уровень) известно даже без имени события: публикующий есть → orphaned не срабатывает
        var g = PubSubGraphBuilder.Enrich(Graph("orders", "mail"), [Analysis("orders", Pub("", 0.5)), Analysis("mail", Sub())]);

        Assert.DoesNotContain(g.Problems, x => x.Kind == "orphaned-subscription");
    }

    [Fact]
    public void Items_are_capped()
    {
        var many = Enumerable.Range(0, 100).Select(i => Pub($"E{i}", 0.8, i + 1)).ToArray();
        var g = PubSubGraphBuilder.Enrich(Graph("orders"), [Analysis("orders", many)]);

        Assert.InRange(g.Problems.Single(x => x.Kind == "dangling-event").Items!.Count, 1, 20);
    }

    [Fact]
    public void No_bus_sites_returns_graph_unchanged()
    {
        var g0 = Graph("orders");
        Assert.Same(g0, PubSubGraphBuilder.Enrich(g0, [Analysis("orders")]));
    }
}

public class PubSubScopeTests
{
    private static SystemNode Svc(string id, string name) => new(id, name, SystemNodeKind.Service, name + ":1");
    private static readonly SystemNode Bus = new("sys/rabbit", "rabbit", SystemNodeKind.Topic, "rabbitmq:3");
    private static BusSite Pub() => new(BusDirection.Publish, "E", "/r/P.cs", 3, BusBasis.Marker, 0.8);

    private static ServiceAnalysis Analysis(string name, string? nodeId, params BusSite[] sites) =>
        new(new DiscoveredService(name, $"/r/{name}", $"/r/{name}/docker-compose.yml"), "sys", new AnalysisResult([], [], new Coverage(0, 0, 0), []))
        { NodeId = nodeId, BusSites = sites };

    [Fact]
    public void Service_with_bus_code_but_no_uses_bus_downgrades_findings_to_coverage()
    {
        // второй сервис с кодом шины не подключён к ней в compose (адрес в appsettings/vault) — утверждать «никто не читает» нельзя
        var g = new SystemGraph([Svc("sys/orders", "orders"), Svc("sys/mail", "mail"), Bus],
            [new SystemEdge("sys/orders", Bus.Id, "uses-bus", EdgeVia.Config, 0.7)], []);

        var r = PubSubGraphBuilder.Enrich(g, [Analysis("orders", "sys/orders", Pub()), Analysis("mail", "sys/mail", Pub())]);

        Assert.Equal(ProblemSeverity.Coverage, Assert.Single(r.Problems, p => p.Kind == "dangling-event").Severity);
    }

    [Fact]
    public void Uses_bus_to_external_broker_downgrades_findings_to_coverage()
    {
        var ext = new SystemNode("ext:broker", "broker", SystemNodeKind.External);
        var g = new SystemGraph([Svc("sys/orders", "orders"), Svc("sys/mail", "mail"), Bus, ext],
            [new SystemEdge("sys/orders", Bus.Id, "uses-bus", EdgeVia.Config, 0.7), new SystemEdge("sys/mail", ext.Id, "uses-bus", EdgeVia.Config, 0.4)], []);

        var r = PubSubGraphBuilder.Enrich(g, [Analysis("orders", "sys/orders", Pub()), Analysis("mail", "sys/mail", Pub("X"))]);

        Assert.All(r.Problems.Where(p => p.Kind == "dangling-event"), p => Assert.Equal(ProblemSeverity.Coverage, p.Severity));
    }

    [Fact]
    public void Service_with_bus_code_not_bound_to_a_node_downgrades_findings_to_coverage()
    {
        // «ghost» не привязывается к узлу (2 одинаково годных узла в системе, имя не совпало) — его код шины нельзя отбросить молча
        var g = new SystemGraph([Svc("sys/orders", "orders"), Svc("sys/mail", "mail"), Bus],
            [new SystemEdge("sys/orders", Bus.Id, "uses-bus", EdgeVia.Config, 0.7)], []);

        var r = PubSubGraphBuilder.Enrich(g, [Analysis("orders", "sys/orders", Pub()), Analysis("ghostworker", null, Pub("X"))]);

        var p = Assert.Single(r.Problems, p => p.Kind == "dangling-event");
        Assert.Equal(ProblemSeverity.Coverage, p.Severity);
        Assert.Contains("без подключения", p.Title);
    }

    private static BusSite Pub(string name) => new(BusDirection.Publish, name, "/r/P.cs", 3, BusBasis.Marker, 0.8);

    [Fact]
    public void Truncated_scan_keeps_silence()
    {
        var g = new SystemGraph([Svc("sys/orders", "orders"), Bus], [new SystemEdge("sys/orders", Bus.Id, "uses-bus", EdgeVia.Config, 0.7)], []);

        var r = PubSubGraphBuilder.Enrich(g, [Analysis("orders", "sys/orders", Pub(), new BusSite(BusDirection.Unknown, "", "/r", 1, BusBasis.Truncated, 0))]);

        Assert.DoesNotContain(r.Problems, p => p.Kind == "dangling-event");
    }

    [Fact]
    public void Several_nodes_in_system_bind_to_the_real_service_not_the_satellite()
    {
        // монорепо без NodeId: источник выбирается по системе; сателлит (migrator) не должен получать рёбра
        var g = new SystemGraph([Svc("sys/app", "app"), Svc("sys/app-migrator", "app-migrator"), Bus],
            [new SystemEdge("sys/app", Bus.Id, "uses-bus", EdgeVia.Config, 0.7)], []);

        var r = PubSubGraphBuilder.Enrich(g, [Analysis("app", null, Pub())]);

        Assert.Contains(r.Edges, e => e is { Kind: "publishes", Source: "sys/app" });
        Assert.DoesNotContain(r.Edges, e => e.Kind == "publishes" && e.Source == "sys/app-migrator");
    }
}

// Сквозная проводка: флаг calls доводит вертикаль до чтения шины и до проблем в DTO; без флага код шины не читается.
public class PubSubEndToEndTests
{
    private sealed class FakeSource(SystemGraph graph) : ISystemSource
    {
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default) => graph;
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class FakeTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => [];
    }

    private sealed class SpyBus(params BusSite[] sites) : IBusSource
    {
        public int Reads { get; private set; }
        public IReadOnlyList<BusSite> Read(string root, CancellationToken ct = default) { Reads++; return sites; }
    }

    private static (GetSystemHandler Handler, SpyBus Spy, string Dir) Setup()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_pubsub_" + Guid.NewGuid().ToString("N"))).FullName;
        var spy = new SpyBus(new BusSite(BusDirection.Publish, "OrderCreated", "/r/P.cs", 3, BusBasis.Marker, 0.8));
        var analyzer = new ServiceAnalyzer(
            new FakeDiscovery([new DiscoveredService("Svc.A", Path.Combine(dir, "svc-a"), "docker-compose.yml")]),
            new FakeTypes(), new TypeClassifier(), bus: spy);
        var graph = new SystemGraph(
            [new SystemNode("svc-a/app", "app", SystemNodeKind.Service), new SystemNode("svc-a/rabbit", "rabbit", SystemNodeKind.Topic)],
            [new SystemEdge("svc-a/app", "svc-a/rabbit", "uses-bus", EdgeVia.Config, 0.8)], []);
        return (new GetSystemHandler(new FakeSource(graph), analyzer), spy, dir);
    }

    [Fact]
    public void Calls_flag_reads_bus_and_reports_dangling_event()
    {
        var (handler, spy, dir) = Setup();
        try
        {
            var dto = handler.Handle(new GetSystemQuery(dir, Calls: true))!;

            Assert.True(spy.Reads > 0);
            Assert.Contains(dto.Edges, e => e.Kind == "publishes");
            Assert.Contains(dto.Problems, p => p.Kind == "dangling-event");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Without_calls_flag_bus_is_not_read()
    {
        var (handler, spy, dir) = Setup();
        try
        {
            handler.Handle(new GetSystemQuery(dir, Drift: true));

            Assert.Equal(0, spy.Reads);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
