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
    public void Cycle_reports_all_members_and_min_confidence_and_colors_every_node()
    {
        var g = new SystemGraph([Svc("a"), Svc("b"), Svc("c"), Svc("d")],
            [Http("a", "b"), Http("b", "c"), new SystemEdge("c", "a", "http-call", EdgeVia.Code, 0.9), Http("c", "d")], []);

        var p = Assert.Single(ServiceCycleDetector.Detect(g));
        Assert.Equal(["a", "b", "c"], p.Members);
        Assert.Equal(0.8, p.Confidence);

        var dto = (g with { Problems = [p] }).ToGraphDto("t");
        Assert.All(dto.Nodes.Where(n => n.Id is "a" or "b" or "c"), n => Assert.Equal("critical", n.Health));
        Assert.Equal("ok", dto.Nodes.Single(n => n.Id == "d").Health);
        Assert.Equal(["a", "b", "c"], dto.Problems![0].Members);
        Assert.NotNull(dto.Problems[0].Fix);
    }

    [Fact]
    public void Low_confidence_edges_do_not_form_cycle_and_threshold_is_tunable()
    {
        var g = new SystemGraph([Svc("a"), Svc("b")],
            [new SystemEdge("a", "b", "http-call", EdgeVia.Code, 0.5), new SystemEdge("b", "a", "http-call", EdgeVia.Code, 0.5)], []);

        Assert.Empty(ServiceCycleDetector.Detect(g));
        Assert.Single(ServiceCycleDetector.Detect(g, minConfidence: 0.5));
    }

    [Theory]
    [InlineData("unattributed-calls")]
    [InlineData("ambiguous-target")]
    public void Coverage_kinds_have_fix_hint(string kind)
    {
        var dto = new SystemProblem(ProblemSeverity.Coverage, kind, "t", "x").ToProblemDto(0);
        Assert.False(string.IsNullOrEmpty(dto.Fix));
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

    [Fact]
    public void Ignores_low_confidence_cross_system_edges()
    {
        // цель разрешена лишь по совпадению имени между несвязанными системами (conf 0.5) →
        // не выдумываем громкий Critical (ревью #20 — находка Critical о ложном цикле)
        var g = new SystemGraph([Svc("sysA/checkout"), Svc("sysB/payments")],
        [
            new SystemEdge("sysA/checkout", "sysB/payments", "http-call", EdgeVia.Config, 0.5),
            new SystemEdge("sysB/payments", "sysA/checkout", "http-call", EdgeVia.Config, 0.5),
        ], []);

        Assert.Empty(ServiceCycleDetector.Detect(g));
    }

    [Fact]
    public void Reports_each_independent_cycle_separately()
    {
        // два несвязанных цикла a↔b и c↔d → две отдельные проблемы
        var g = new SystemGraph([Svc("a"), Svc("b"), Svc("c"), Svc("d")],
            [Http("a", "b"), Http("b", "a"), Http("c", "d"), Http("d", "c")], []);

        var p = ServiceCycleDetector.Detect(g);
        Assert.Equal(2, p.Count);
        Assert.All(p, x => Assert.Equal("service-cycle", x.Kind));
    }

    [Fact]
    public void Self_loop_is_not_a_cycle()
    {
        // сервис, зовущий сам себя, — не цикл зависимостей (компонента размера 1)
        var g = new SystemGraph([Svc("a")], [Http("a", "a")], []);

        Assert.Empty(ServiceCycleDetector.Detect(g));
    }

    [Fact]
    public void Deep_chain_does_not_overflow_and_finds_tail_cycle()
    {
        // длинная цепочка вызовов s0→s1→…→sN: рекурсивный Тарьян переполнил бы стек, итеративный — нет (P1.5-H).
        const int n = 100_000;
        var nodes = new List<SystemNode>(n);
        var edges = new List<SystemEdge>(n);
        for (var k = 0; k < n; k++)
        {
            nodes.Add(Svc($"s{k}"));
            if (k > 0) edges.Add(Http($"s{k - 1}", $"s{k}"));
        }
        edges.Add(Http($"s{n - 1}", $"s{n - 2}")); // замыкаем ХВОСТ в 2-цикл {s(n-2), s(n-1)}

        var p = ServiceCycleDetector.Detect(new SystemGraph(nodes, edges, []));

        var cycle = Assert.Single(p); // остальная цепочка — DAG, цикл ровно один
        Assert.Equal(ProblemSeverity.Critical, cycle.Severity);
        Assert.Contains($"s{n - 1}", cycle.Title);
    }
}
