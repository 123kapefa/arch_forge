using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class SystemGraphMapperTests
{
    [Fact]
    public void Maps_nodes_edges_problems_and_health()
    {
        var g = new SystemGraph(
            [
                new SystemNode("s/api", "api", SystemNodeKind.Service, "api:1"),
                new SystemNode("s/db", "db", SystemNodeKind.Datastore, "postgres:16"),
                new SystemNode("http://sms", "sms", SystemNodeKind.External),
            ],
            [
                new SystemEdge("s/api", "s/db", "rw-db", EdgeVia.Config, 0.9),
                new SystemEdge("s/api", "http://sms", "http-call", EdgeVia.Config, 0.4),
            ],
            [new SystemProblem(ProblemSeverity.Critical, "shared-db", "общая БД", "s/db")]);

        var dto = SystemGraphMapper.ToDto(g, "sys");

        Assert.Equal("sys", dto.Project);
        Assert.Contains(dto.Nodes, n => n.Id == "s/api" && n.Kind == "service" && n.Health == "ok");
        Assert.Contains(dto.Nodes, n => n.Id == "s/db" && n.Kind == "datastore" && n.Health == "critical"); // location проблемы
        Assert.Contains(dto.Nodes, n => n.Kind == "external");
        Assert.Contains(dto.Edges, e => e is { Source: "s/api", Target: "s/db", Kind: "rw-db", Via: "config" } && e.Confidence == 0.9);
        Assert.Contains(dto.Edges, e => e.Kind == "http-call");
        Assert.Equal("critical", dto.Health);
        Assert.NotNull(dto.Problems);
        Assert.Contains(dto.Problems!, p => p is { Kind: "shared-db", Severity: "critical", Location: "s/db" });
    }

    [Fact]
    public void Clean_graph_has_ok_health_and_no_problems_field()
    {
        var g = new SystemGraph([new SystemNode("s/api", "api", SystemNodeKind.Service, "api:1")], [], []);

        var dto = SystemGraphMapper.ToDto(g, "sys");

        Assert.Equal("ok", dto.Health);
        Assert.Null(dto.Problems); // пусто → null (не пишется в JSON)
    }
}
