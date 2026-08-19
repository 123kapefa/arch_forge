using System.Text.Json;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Tests.Systems;

// Аддитивность контракта Фазы 1.5: клиенты Фазы 1 не ломаются.
public class ContractBackCompatTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Phase1_json_without_new_fields_deserializes()
    {
        const string json = """
            {"project":"svc",
             "nodes":[{"id":"1","name":"Foo","kind":"class","layer":"Domain","role":"Aggregate","via":"marker","file":"Foo.cs","drift":[]}],
             "edges":[{"id":"e1","source":"1","target":"2","kind":"holds"}],
             "coverage":{"marker":1,"heuristic":0,"unknown":0,"total":1},
             "drift":[]}
            """;

        var g = JsonSerializer.Deserialize<GraphDto>(json, Web)!;

        Assert.Equal("svc", g.Project);
        Assert.Single(g.Nodes);
        Assert.Null(g.Nodes[0].Service);   // новые опц. поля отсутствуют в JSON → null
        Assert.Null(g.Nodes[0].Health);
        Assert.Null(g.Edges[0].Confidence);
        Assert.Null(g.Problems);
    }

    [Fact]
    public void Phase1_shaped_dto_serializes_with_exactly_phase1_keys()
    {
        var g = new GraphDto("svc",
            [new NodeDto("1", "Foo", "class", "Domain", "Aggregate", "marker", "Foo.cs", [])],
            [new EdgeDto("e1", "1", "2", "holds")],
            new CoverageDto(1, 0, 0, 1), []);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(g, Web));
        var root = doc.RootElement;

        // Ровно поля Фазы 1 — ни одно системное поле не просочилось (исчерпывающая проверка байт-совместимости).
        AssertKeys(root, "project", "nodes", "edges", "coverage", "drift");
        AssertKeys(root.GetProperty("nodes")[0], "id", "name", "kind", "layer", "role", "via", "file", "drift");
        AssertKeys(root.GetProperty("edges")[0], "id", "source", "target", "kind");
    }

    private static void AssertKeys(JsonElement obj, params string[] expected) =>
        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            obj.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void System_level_fields_round_trip()
    {
        var g = new GraphDto("sys", [], [], new CoverageDto(0, 0, 0, 0), [],
            Health: "critical",
            Problems: [new ProblemDto("p1", "critical", "shared-db", "общая БД", "db", Via: "config", Fix: "свой контекст")]);

        var back = JsonSerializer.Deserialize<GraphDto>(JsonSerializer.Serialize(g, Web), Web)!;

        Assert.Equal("critical", back.Health);
        Assert.Single(back.Problems!);
        Assert.Equal("shared-db", back.Problems![0].Kind);
        Assert.Equal("config", back.Problems![0].Via);
    }

    [Fact]
    public void Node_with_members_round_trips()
    {
        var node = new NodeDto("c1", "AuthController", "type", "API", "Controller", "marker", "AuthController.cs", [],
            Service: "auth", Level: "type", Health: "ok",
            Members: [new MemberDto("m1", "POST /login", "endpoint", HttpMethod: "POST", Route: "/login")]);

        var back = JsonSerializer.Deserialize<NodeDto>(JsonSerializer.Serialize(node, Web), Web)!;

        Assert.Equal("auth", back.Service);
        Assert.Single(back.Members!);
        Assert.Equal("POST", back.Members![0].HttpMethod);
    }

    [Fact]
    public void Node_endpoints_round_trip()  // #71
    {
        var node = new NodeDto("gw", "api-gateway", "service", "", "", "", "", [],
            Level: "service", Health: "ok",
            Endpoints: [new EndpointDto("GET", "/api/auth/csrf", "minimal", Line: 20),
                        new EndpointDto("POST", "/audit/record", "controller", Type: "AuditController")]);

        var back = JsonSerializer.Deserialize<NodeDto>(JsonSerializer.Serialize(node, Web), Web)!;

        Assert.Equal(2, back.Endpoints!.Count);
        Assert.Equal("minimal", back.Endpoints![0].Source);
        Assert.Equal("/api/auth/csrf", back.Endpoints![0].Route);
        Assert.Equal("AuditController", back.Endpoints![1].Type);
    }

    [Fact]
    public void Environment_dto_round_trips()  // зарезервированная запись — smoke
    {
        var back = JsonSerializer.Deserialize<EnvironmentDto>(
            JsonSerializer.Serialize(new EnvironmentDto("prod", "Production", "instance"), Web), Web)!;
        Assert.Equal("Production", back.Name);
        Assert.Equal("instance", back.Kind);
    }

    [Fact]
    public void Service_summary_round_trips()
    {
        var g = new GraphDto("sys", [], [], new CoverageDto(0, 0, 0, 0), [],
            Services: [new ServiceSummaryDto("auth", "AuthService", "warning",
                [new LayerSummaryDto("Domain", "warning", 9, Note: "Domain → Infrastructure")])]);

        var back = JsonSerializer.Deserialize<GraphDto>(JsonSerializer.Serialize(g, Web), Web)!;

        var svc = Assert.Single(back.Services!);
        Assert.Equal("AuthService", svc.Name);
        var layer = Assert.Single(svc.Layers);
        Assert.Equal("Domain", layer.Name);
        Assert.Equal(9, layer.Types);
        Assert.Contains("Infrastructure", layer.Note);
    }

    [Fact]
    public void Node_layers_round_trip()
    {
        var node = new NodeDto("svc", "catalog-api", "service", "", "", "", "", [],
            Level: "service", Health: "warning",
            Layers: [new LayerSummaryDto("Application", "warning", 44, Note: "Application → Infrastructure")]);

        var back = JsonSerializer.Deserialize<NodeDto>(JsonSerializer.Serialize(node, Web), Web)!;

        var layer = Assert.Single(back.Layers!);
        Assert.Equal("Application", layer.Name);
        Assert.Equal(44, layer.Types);
        // без слоёв поле опускается (Ф1-совместимость)
        Assert.DoesNotContain("layers", JsonSerializer.Serialize(
            new NodeDto("n", "N", "class", "Domain", "Aggregate", "marker", "N.cs", []), Web));
    }

    [Fact]
    public void Services_omitted_when_null()
    {
        var json = JsonSerializer.Serialize(new GraphDto("s", [], [], new CoverageDto(0, 0, 0, 0), []), Web);
        Assert.DoesNotContain("services", json); // Ф1-байт-совместимость: поле не пишется при null
    }

    [Fact]
    public void Degradations_round_trip_and_omitted_when_null()
    {
        // present → сериализуется и читается (P1.5-H: «карта неполная»)
        var g = new GraphDto("sys", [], [], new CoverageDto(0, 0, 0, 0), [],
            Degradations: ["сервис «AuthService» не разобрался — часть карты пропущена"]);
        var back = JsonSerializer.Deserialize<GraphDto>(JsonSerializer.Serialize(g, Web), Web)!;
        Assert.Contains("AuthService", Assert.Single(back.Degradations!));

        // null → поле опущено (Ф1-байт-совместимость)
        Assert.DoesNotContain("degradations",
            JsonSerializer.Serialize(new GraphDto("s", [], [], new CoverageDto(0, 0, 0, 0), []), Web));
    }

    [Fact]
    public void Problem_category_is_additive()
    {
        // линза сериализуется; отсутствие (null) опускается → старый JSON без category валиден
        var withCat = new ProblemDto("p1", "info", "drift", "дрейф", "svc", Category: "convention");
        var json = JsonSerializer.Serialize(withCat, Web);
        Assert.Contains("\"category\":\"convention\"", json);
        Assert.DoesNotContain("category", JsonSerializer.Serialize(
            new ProblemDto("p2", "warning", "shared-db", "БД", "db"), Web));

        Assert.Equal("convention", JsonSerializer.Deserialize<ProblemDto>(json, Web)!.Category);
    }
}
