using ArchForge.Contracts;
using ArchForge.Map.Core;
using Xunit;

namespace ArchForge.Map.Tests;

public class FlowChainBuilderTests
{
    private static NodeDto Svc(string id, params EndpointDto[] eps) =>
        new(id, id, "service", "", "", "", "", [], Endpoints: eps.Length > 0 ? eps : null);
    private static NodeDto Other(string id, string kind) => new(id, id, kind, "", "", "", "", []);
    private static EdgeDto Http(string a, string b, params HttpCallDto[] calls) =>
        new($"{a}-http-call->{b}", a, b, "http-call", "code", 0.9, Calls: calls.Length > 0 ? calls : null);
    private static EdgeDto E(string a, string b, string kind) => new($"{a}-{kind}->{b}", a, b, kind);
    private static GraphDto G(NodeDto[] n, EdgeDto[] e, string[]? deg = null) =>
        new("p", n, e, new CoverageDto(0, 0, 0, 0), [], Degradations: deg);

    [Fact]
    public void Unknown_or_nonservice_entry_is_null()
    {
        var g = G([Svc("a"), Other("db", "datastore")], []);
        Assert.Null(FlowChainBuilder.Build(g, "nope"));
        Assert.Null(FlowChainBuilder.Build(g, "db"));
    }

    [Fact]
    public void Chain_goes_gateway_cart_orders_then_db_and_bus()
    {
        var g = G(
            [Svc("gw"), Svc("cart"), Svc("orders"), Other("pg", "datastore"), Other("bus", "topic"), Svc("mail")],
            [Http("gw", "cart", new HttpCallDto("POST", "/cart/checkout", Matched: true)),
             Http("cart", "orders", new HttpCallDto("POST", "/orders", Matched: true)),
             E("orders", "pg", "rw-db"), E("orders", "bus", "publishes"), E("mail", "bus", "subscribes")]);

        var f = FlowChainBuilder.Build(g, "gw")!;

        Assert.Equal(["cart", "orders", "bus", "mail", "pg"], f.Steps.Select(s => s.To));
        Assert.Equal([1, 2, 3, 4, 3], f.Steps.Select(s => s.Depth));
        Assert.Equal(["http", "http", "publish", "consume", "data"], f.Steps.Select(s => s.Kind));
        Assert.True(f.Steps[0].Sync);
        Assert.False(f.Steps[2].Sync);
        Assert.Equal(f.Steps[2].Index, f.Steps[3].Parent);
        Assert.False(f.Truncated);
        Assert.Contains(f.Notes, n => n.Contains("без рантайма"));
    }

    [Fact]
    public void Cycle_is_marked_not_followed()
    {
        var g = G([Svc("a"), Svc("b")],
            [Http("a", "b", new HttpCallDto("GET", "/x")), Http("b", "a", new HttpCallDto("GET", "/y"))]);

        var f = FlowChainBuilder.Build(g, "a")!;

        Assert.Equal(2, f.Steps.Count);
        Assert.Equal("cycle", f.Steps[1].Status);
    }

    [Fact]
    public void Diamond_second_visit_is_repeat()
    {
        var g = G([Svc("a"), Svc("b"), Svc("c"), Svc("d"), Other("pg", "datastore")],
            [Http("a", "b", new HttpCallDto("GET", "/b")), Http("a", "c", new HttpCallDto("GET", "/c")),
             Http("b", "d", new HttpCallDto("GET", "/d")), Http("c", "d", new HttpCallDto("GET", "/d")),
             E("d", "pg", "rw-db")]);

        var f = FlowChainBuilder.Build(g, "a")!;

        Assert.Single(f.Steps, s => s.Kind == "data");
        Assert.Single(f.Steps, s => s.Status == "repeat");
    }

    [Fact]
    public void Depth_limit_marks_truncated()
    {
        var g = G([Svc("a"), Svc("b"), Svc("c"), Svc("d")],
            [Http("a", "b", new HttpCallDto("GET", "/")), Http("b", "c", new HttpCallDto("GET", "/")), Http("c", "d", new HttpCallDto("GET", "/"))]);

        var f = FlowChainBuilder.Build(g, "a", maxDepth: 2)!;

        Assert.True(f.Truncated);
        Assert.Equal("truncated", f.Steps[^1].Status);
        Assert.DoesNotContain(f.Steps, s => s.To == "d");
        Assert.Contains(f.Notes, n => n.Contains("лимит глубины"));
    }

    [Fact]
    public void Step_cap_stops_and_says_so()
    {
        var nodes = new List<NodeDto> { Svc("a") };
        var edges = new List<EdgeDto>();
        for (var i = 0; i < 10; i++) { nodes.Add(Other($"db{i}", "datastore")); edges.Add(E("a", $"db{i}", "rw-db")); }

        var f = FlowChainBuilder.Build(G([.. nodes], [.. edges]), "a", maxSteps: 3)!;

        Assert.Equal(3, f.Steps.Count);
        Assert.True(f.Truncated);
        Assert.Contains(f.Notes, n => n.Contains("лимит шагов"));
    }

    [Fact]
    public void Unresolved_things_stay_visible()
    {
        var g = G([Svc("a"), Svc("b"), Svc("c"), Other("bus", "topic"), Other("ext", "external")],
            [Http("a", "b", new HttpCallDto("GET", "/nope", Matched: false)), Http("a", "c"),
             Http("a", "ext", new HttpCallDto("POST", "/pay")), E("a", "bus", "uses-bus")],
            deg: ["сервис «x» не разобрался"]);

        var f = FlowChainBuilder.Build(g, "a")!;

        Assert.Equal("unmatched", f.Steps.Single(s => s.To == "b").Status);
        Assert.Equal("unresolved", f.Steps.Single(s => s.To == "c").Status);
        Assert.Equal("external", f.Steps.Single(s => s.To == "ext").Kind);
        Assert.Equal("unresolved", f.Steps.Single(s => s.To == "bus").Status);
        Assert.Contains(f.Notes, n => n.StartsWith("Карта неполная"));
    }

    [Fact]
    public void Publisher_without_subscribers_and_known_entry_endpoint()
    {
        var g = G([Svc("a", new EndpointDto("GET", "/orders", "controller")), Other("bus", "topic")], [E("a", "bus", "publishes")]);

        var f = FlowChainBuilder.Build(g, "a", "get /orders")!;

        Assert.Equal("GET /orders", f.Endpoint);
        Assert.Equal("unresolved", f.Steps[0].Status);
        Assert.Contains(f.Notes, n => n.Contains("без сопоставления имени события"));
        Assert.Contains(FlowChainBuilder.Build(g, "a", "GET /zzz")!.Notes, n => n.Contains("не найден"));
    }
}
