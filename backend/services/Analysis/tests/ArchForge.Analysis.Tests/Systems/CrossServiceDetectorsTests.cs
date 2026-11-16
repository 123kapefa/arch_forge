using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class CrossServiceDetectorsTests
{
    private static SystemNode Svc(string id) => new(id, id, SystemNodeKind.Service, id + ":1");
    private static SystemEdge Http(string a, string b, double conf = 0.9, bool deferred = false, IReadOnlyList<HttpCall>? calls = null) =>
        new(a, b, "http-call", EdgeVia.Code, conf, deferred, calls);
    private static SystemGraph G(string[] ids, params SystemEdge[] edges) => new(ids.Select(Svc).ToList(), edges, []);

    // ── sync-chain ──
    [Fact]
    public void Chain_of_four_services_fires_with_all_members_and_min_confidence()
    {
        var g = G(["a", "b", "c", "d"], Http("a", "b", 0.9), Http("b", "c", 0.85), Http("c", "d", 0.9));
        var p = Assert.Single(CrossServiceDetectors.SyncChains(g));
        Assert.Equal("sync-chain", p.Kind);
        Assert.Equal(ProblemSeverity.Warning, p.Severity);
        Assert.Equal(["a", "b", "c", "d"], p.Members);
        Assert.Equal(0.85, p.Confidence);
    }

    [Fact]
    public void Chain_of_three_does_not_fire() =>
        Assert.Empty(CrossServiceDetectors.SyncChains(G(["a", "b", "c"], Http("a", "b"), Http("b", "c"))));

    [Fact]
    public void Chain_ignores_weak_and_deferred_edges() =>
        Assert.Empty(CrossServiceDetectors.SyncChains(G(["a", "b", "c", "d"],
            Http("a", "b"), Http("b", "c", conf: 0.5), Http("c", "d", deferred: true))));

    [Fact]
    public void Chain_with_cycle_terminates_and_uses_acyclic_part()
    {
        // a→b→c→d→b (цикл b-c-d): цикл ловит service-cycle, цепочка не зацикливается
        var g = G(["a", "b", "c", "d"], Http("a", "b"), Http("b", "c"), Http("c", "d"), Http("d", "b"));
        var p = Assert.Single(CrossServiceDetectors.SyncChains(g));
        Assert.Equal(["a", "b", "c", "d"], p.Members);
    }

    [Fact]
    public void Pure_cycle_has_no_entry_and_gives_no_chain() =>
        Assert.Empty(CrossServiceDetectors.SyncChains(G(["a", "b", "c", "d"], Http("a", "b"), Http("b", "c"), Http("c", "d"), Http("d", "a"))));

    [Fact]
    public void Chain_count_is_capped_and_truncation_is_visible()
    {
        var n = CrossServiceDetectors.MaxPerKind + 3;
        var ids = new List<string>(); var edges = new List<SystemEdge>();
        for (var i = 0; i < n; i++)
        {
            string[] c = [$"a{i:00}", $"b{i:00}", $"c{i:00}", $"d{i:00}"];
            ids.AddRange(c);
            edges.Add(Http(c[0], c[1])); edges.Add(Http(c[1], c[2])); edges.Add(Http(c[2], c[3]));
        }
        var ps = CrossServiceDetectors.SyncChains(G([.. ids], [.. edges]));
        Assert.Equal(CrossServiceDetectors.MaxPerKind, ps.Count(p => p.Kind == "sync-chain"));
        Assert.Contains(ps, p => p is { Kind: "sync-chain-truncated", Severity: ProblemSeverity.Coverage });
    }

    // ── sync-fan-out ──
    private static SystemGraph Fan(string hub, int n)
    {
        var ids = new[] { hub }.Concat(Enumerable.Range(0, n).Select(i => $"t{i}")).ToArray();
        return G(ids, [.. Enumerable.Range(0, n).Select(i => Http(hub, $"t{i}"))]);
    }

    [Fact]
    public void FanOut_fires_at_threshold() =>
        Assert.Equal("sync-fan-out", Assert.Single(CrossServiceDetectors.FanOut(Fan("orders", CrossServiceDetectors.FanOutMin))).Kind);

    [Fact]
    public void FanOut_below_threshold_is_silent() =>
        Assert.Empty(CrossServiceDetectors.FanOut(Fan("orders", CrossServiceDetectors.FanOutMin - 1)));

    [Fact]
    public void FanOut_skips_gateway_and_bff() =>
        Assert.All(new[] { "api-gateway", "web-bff" }, h => Assert.Empty(CrossServiceDetectors.FanOut(Fan(h, 8))));

    [Fact]
    public void FanOut_counts_distinct_targets_not_calls_and_ignores_non_services()
    {
        var edges = new List<SystemEdge>(Enumerable.Range(0, 4).Select(i => Http("o", $"t{i}")))
        {
            Http("o", "t0"),                                              // дубль цели
            new SystemEdge("o", "db", "rw-db", EdgeVia.Compose, 1.0),     // не http-call
        };
        var g = new SystemGraph([Svc("o"), Svc("t0"), Svc("t1"), Svc("t2"), Svc("t3"), new SystemNode("db", "db", SystemNodeKind.Datastore)], edges, []);
        Assert.Empty(CrossServiceDetectors.FanOut(g));
    }

    // ── contract-gap ──
    private static HttpCall Call(string route, bool? matched, double conf = 0.9) =>
        new("GET", route, conf, matched, "Client.cs", 12, "http.GetAsync(...)");

    [Fact]
    public void ContractGap_lists_only_unmatched_calls_with_source()
    {
        var g = G(["a", "b"], Http("a", "b", calls: [Call("/v1/users", false), Call("/v2/users", true), Call("/x", null)]));
        var p = Assert.Single(CrossServiceDetectors.ContractGaps(g));
        Assert.Equal(ProblemSeverity.Warning, p.Severity);
        var it = Assert.Single(p.Items!);
        Assert.Equal("GET /v1/users", it.Name);
        Assert.Equal("Client.cs", it.File);
        Assert.Equal(12, it.Line);
    }

    [Fact]
    public void ContractGap_is_silent_when_all_matched_or_unknown() =>
        Assert.Empty(CrossServiceDetectors.ContractGaps(G(["a", "b"], Http("a", "b", calls: [Call("/a", true), Call("/b", null)]))));

    [Fact]
    public void ContractGap_weak_confidence_is_info_only() =>
        Assert.Equal(ProblemSeverity.Info,
            Assert.Single(CrossServiceDetectors.ContractGaps(G(["a", "b"], Http("a", "b", conf: 0.6, calls: [Call("/v1", false, 0.6)])))).Severity);

    [Fact]
    public void ContractGap_items_are_capped()
    {
        var calls = Enumerable.Range(0, CrossServiceDetectors.MaxItems + 10).Select(i => Call($"/r{i:000}", false)).ToList();
        var p = Assert.Single(CrossServiceDetectors.ContractGaps(G(["a", "b"], Http("a", "b", calls: calls))));
        Assert.Equal(CrossServiceDetectors.MaxItems, p.Items!.Count);
        Assert.Contains((CrossServiceDetectors.MaxItems + 10).ToString(), p.Title);
    }

    [Fact]
    public void Aggregate_runs_all_three_and_new_kinds_are_in_catalog_with_fix_hint()
    {
        var g = G(["a", "b", "c", "d"], Http("a", "b", calls: [Call("/gone", false)]), Http("b", "c"), Http("c", "d"));
        var ps = CrossServiceDetectors.Detect(g);
        Assert.Contains(ps, p => p.Kind == "sync-chain");
        Assert.Contains(ps, p => p.Kind == "contract-gap");
        var dto = (g with { Problems = ps }).ToGraphDto("t");
        Assert.All(dto.Problems!, p => Assert.NotNull(p.Fix));
        foreach (var k in new[] { "sync-chain", "sync-fan-out", "contract-gap" })
            Assert.NotNull(ArchForge.Analysis.Core.Profiles.RuleCatalog.Find(k));
    }
}
