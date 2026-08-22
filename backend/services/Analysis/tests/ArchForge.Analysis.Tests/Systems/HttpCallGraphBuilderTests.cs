using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Tests.Systems;

// E3.1 срез 1: обогащение графа конкретными http-вызовами из кода (call-graph).
// Источник атрибутируется по системе анализа (SystemOf(location)) — не зависит от классификации слоёв.
public class HttpCallGraphBuilderTests
{
    // два сервиса в одной системе «sys»: orders → catalog
    private static SystemGraph TwoServices(params SystemEdge[] edges) => new(
        Nodes:
        [
            new SystemNode("sys/orders", "orders", SystemNodeKind.Service),
            new SystemNode("sys/catalog", "catalog", SystemNodeKind.Service),
        ],
        Edges: edges,
        Problems: []);

    // nodeLayers с эндпоинтами цели (catalog) для RouteMatch — опционально
    private static Dictionary<string, MicroserviceLayerBuilder.Entry> Layers(params EndpointDto[] catalogEndpoints) => new(StringComparer.Ordinal)
    {
        [MicroserviceLayerBuilder.NodeKey("sys/catalog", "catalog")] = new("ok", "/repo/catalog", [], catalogEndpoints),
    };

    // имя .sln «Orders» → тай-брейк источника среди узлов системы «sys» находит узел orders
    private static ServiceAnalysis Orders(params HttpCallHit[] calls) =>
        new(new DiscoveredService("Orders", "/repo/orders", "compose.yml"), "sys", Result: null) { HttpCalls = calls };

    private static HttpCallHit Call(string method, string url) => new(method, url, "/repo/orders/Client.cs", 12, $"{method} {url}");

    [Fact]
    public void Adds_code_edge_for_call_to_service_in_system()
    {
        var g = HttpCallGraphBuilder.Enrich(
            TwoServices(),
            [Orders(Call("GET", "http://catalog/api/items"))]);

        var edge = Assert.Single(g.Edges, e => e.Kind == "http-call" && e.Source == "sys/orders" && e.Target == "sys/catalog");
        Assert.Equal(EdgeVia.Code, edge.Via);            // ребра не было в compose → провенанс code
        Assert.Equal(0.9, edge.Confidence);              // цель в той же системе → высокая уверенность
        var call = Assert.Single(edge.Calls!);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/api/items", call.Route);
        Assert.Null(call.Matched);                       // эндпоинты цели не заданы → не сопоставляли
    }

    [Fact]
    public void Merges_calls_onto_existing_edge_keeping_its_provenance()
    {
        var existing = new SystemEdge("sys/orders", "sys/catalog", "http-call", EdgeVia.Config, 0.7);
        var g = HttpCallGraphBuilder.Enrich(
            TwoServices(existing),
            [Orders(Call("GET", "http://catalog/api/items"))]);

        var edge = Assert.Single(g.Edges);               // не добавили второе — дополнили существующее
        Assert.Equal(EdgeVia.Config, edge.Via);          // провенанс config сохранён (дополняем, не заменяем)
        Assert.Equal(0.7, edge.Confidence);
        Assert.Single(edge.Calls!);
    }

    [Fact]
    public void Sets_matched_by_route_when_target_endpoints_known()
    {
        var g = HttpCallGraphBuilder.Enrich(
            TwoServices(),
            [Orders(Call("GET", "http://catalog/api/items/42"), Call("GET", "http://catalog/api/unknown"))],
            Layers(new EndpointDto("GET", "/api/items/{id}", "controller")));

        var edge = Assert.Single(g.Edges, e => e.Kind == "http-call");
        Assert.Equal(2, edge.Calls!.Count);
        Assert.True(edge.Calls!.Single(c => c.Route == "/api/items/42").Matched);    // совпал с {id}
        Assert.False(edge.Calls!.Single(c => c.Route == "/api/unknown").Matched);    // цель есть, эндпоинта нет — честно false
    }

    [Fact]
    public void Skips_external_and_self_calls()
    {
        var g = HttpCallGraphBuilder.Enrich(
            TwoServices(),
            [Orders(
                Call("GET", "http://example.com/api"),   // внешний host → не узел системы
                Call("GET", "http://orders/api/self"))]);  // сам себя → не ребро

        Assert.Empty(g.Edges);
    }

    [Fact]
    public void Picks_app_node_over_migration_sidecar_in_same_system()
    {
        // auth-compose объявляет приложение + мигратор (одна система «auth»); catalog — своя система
        var graph = new SystemGraph(
            [
                new SystemNode("auth/auth-api", "auth-api", SystemNodeKind.Service),
                new SystemNode("auth/auth-migrate", "auth-migrate", SystemNodeKind.Service),
                new SystemNode("catalog/catalog", "catalog", SystemNodeKind.Service),
            ], [], []);
        var sa = new ServiceAnalysis(new DiscoveredService("AuthService", "/repo/auth", "c.yml"), "auth", null)
        { HttpCalls = [Call("GET", "http://catalog/api/items")] };

        var g = HttpCallGraphBuilder.Enrich(graph, [sa]);

        var edge = Assert.Single(g.Edges, e => e.Kind == "http-call");
        Assert.Equal("auth/auth-api", edge.Source);        // сателлит-migrate исключён → остаётся приложение
        Assert.Equal("catalog/catalog", edge.Target);
    }

    [Fact]
    public void Ambiguous_target_raises_coverage_problem_not_edge()  // P2 (adr/0008): не выдумываем, но сигналим
    {
        var graph = new SystemGraph(
            [
                new SystemNode("sys/orders", "orders", SystemNodeKind.Service),
                new SystemNode("sys/catalog", "catalog", SystemNodeKind.Service),
                new SystemNode("sys/catalog-2", "catalog", SystemNodeKind.Service),  // тёзка → неоднозначность цели
            ], [], []);
        var sa = new ServiceAnalysis(new DiscoveredService("Orders", "/repo/orders", "c.yml"), "sys", null)
        { HttpCalls = [Call("GET", "http://catalog/api/items"), Call("POST", "http://catalog/api/items")] };

        var g = HttpCallGraphBuilder.Enrich(graph, [sa]);

        Assert.DoesNotContain(g.Edges, e => e.Kind == "http-call");                  // цель неоднозначна → ребро не рисуем
        var p = Assert.Single(g.Problems, x => x.Kind == "ambiguous-target");        // но и не молчим (дедуп по паре)
        Assert.Equal(ProblemSeverity.Coverage, p.Severity);
        Assert.Equal("sys/orders", p.Location);
    }

    [Fact]
    public void Unattributed_source_raises_coverage_problem()  // P2: вызовы есть, но источник не размещён
    {
        var graph = new SystemGraph(
            [
                new SystemNode("sys/alpha", "alpha", SystemNodeKind.Service),
                new SystemNode("sys/beta", "beta", SystemNodeKind.Service),          // 2 app-узла в системе, имя .sln не совпало
                new SystemNode("cat/catalog", "catalog", SystemNodeKind.Service),
            ], [], []);
        var sa = new ServiceAnalysis(new DiscoveredService("Gamma", "/repo/gamma", "c.yml"), "sys", null)
        { HttpCalls = [Call("GET", "http://catalog/api/items")] };

        var g = HttpCallGraphBuilder.Enrich(graph, [sa]);

        Assert.DoesNotContain(g.Edges, e => e.Kind == "http-call");
        Assert.Contains(g.Problems, x => x.Kind == "unattributed-calls" && x.Severity == ProblemSeverity.Coverage);
    }

    [Fact]
    public void External_node_collision_stays_external_not_inferred()  // P3: не 0.6-Inferred на синтетический external
    {
        var graph = new SystemGraph(
            [
                new SystemNode("sys/orders", "orders", SystemNodeKind.Service),
                new SystemNode("http://legacy-host", "legacy-host", SystemNodeKind.External), // синтетический external от config
            ], [], []);
        var sa = new ServiceAnalysis(new DiscoveredService("Orders", "/repo/orders", "c.yml"), "sys", null)
        { HttpCalls = [Call("GET", "http://legacy-host/api")] };

        var g = HttpCallGraphBuilder.Enrich(graph, [sa]);

        Assert.DoesNotContain(g.Edges, e => e.Kind == "http-call");  // хост вне скана → external → дроп, не Inferred-ребро
    }

    [Fact]
    public void No_calls_returns_graph_unchanged()
    {
        var g0 = TwoServices();
        Assert.Same(g0, HttpCallGraphBuilder.Enrich(g0, []));
        Assert.Same(g0, HttpCallGraphBuilder.Enrich(g0, [Orders()]));
    }

    [Fact]
    public void Attributes_source_even_without_node_layers()  // источник не зависит от классификации слоёв
    {
        var g = HttpCallGraphBuilder.Enrich(TwoServices(), [Orders(Call("GET", "http://catalog/api/items"))], null);
        var edge = Assert.Single(g.Edges, e => e.Kind == "http-call");
        Assert.Single(edge.Calls!);
        Assert.Null(edge.Calls![0].Matched);              // эндпоинты недоступны → matched неизвестен
    }
}
