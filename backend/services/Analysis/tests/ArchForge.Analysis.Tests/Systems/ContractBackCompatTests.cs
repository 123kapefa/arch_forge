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
    public void Environment_dto_round_trips()  // зарезервированная запись — smoke
    {
        var back = JsonSerializer.Deserialize<EnvironmentDto>(
            JsonSerializer.Serialize(new EnvironmentDto("prod", "Production", "instance"), Web), Web)!;
        Assert.Equal("Production", back.Name);
        Assert.Equal("instance", back.Kind);
    }
}
