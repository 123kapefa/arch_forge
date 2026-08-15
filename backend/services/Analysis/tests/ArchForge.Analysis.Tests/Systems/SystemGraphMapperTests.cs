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

    [Fact]
    public void Warning_problem_maps_to_warning_health()
    {
        var g = new SystemGraph(
            [new SystemNode("s/cache", "cache", SystemNodeKind.Datastore, "redis:7")], [],
            [new SystemProblem(ProblemSeverity.Warning, "shared-store", "общий redis", "s/cache")]);

        var dto = SystemGraphMapper.ToDto(g, "sys");

        Assert.Equal("warning", dto.Health);
        Assert.Contains(dto.Nodes, n => n.Id == "s/cache" && n.Health == "warning");
        Assert.Contains(dto.Problems!, p => p is { Severity: "warning", Kind: "shared-store" });
    }

    [Fact]
    public void Coverage_problem_does_not_color_node_or_graph()
    {
        var g = new SystemGraph(
            [new SystemNode("s/web", "web", SystemNodeKind.Service, "web:1")], [],
            [new SystemProblem(ProblemSeverity.Coverage, "ambiguous-target", "неоднозначно", "s/web")]);

        var dto = SystemGraphMapper.ToDto(g, "sys");

        Assert.Contains(dto.Nodes, n => n.Id == "s/web" && n.Health == "ok"); // coverage не красит узел
        Assert.Equal("ok", dto.Health);                                       // и не поднимает health графа
        Assert.Contains(dto.Problems!, p => p.Severity == "coverage");
    }

    [Fact]
    public void Convention_drift_maps_category_info_and_does_not_color_health()
    {
        var g = new SystemGraph(
            [new SystemNode("svc-a/app", "app", SystemNodeKind.Service, "app:1")], [],
            [new SystemProblem(ProblemSeverity.Info, "drift", "«Svc.A»: 4 расхождений", "svc-a/app",
                ProblemCategory.Convention)]);

        var dto = SystemGraphMapper.ToDto(g, "sys");

        var p = Assert.Single(dto.Problems!);
        Assert.Equal("info", p.Severity);
        Assert.Equal("convention", p.Category);                                   // линза для фильтра на фронте
        Assert.Contains(dto.Nodes, n => n.Id == "svc-a/app" && n.Health == "ok"); // дрейф не красит узел
        Assert.Equal("ok", dto.Health);                                           // и не поднимает health системы
    }
}
