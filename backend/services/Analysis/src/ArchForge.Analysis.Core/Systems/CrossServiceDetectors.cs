namespace ArchForge.Analysis.Core.Systems;

// E7 (#92), срез 1: кросс-сервисные детекторы поверх СОБРАННОГО графа (рёбра http-call и их вызовы из call-graph).
// Новых сканов нет — только то, что уже в SystemGraph. Запускается ПОСЛЕ обогащения графа вызовами/pub-sub и ДО применения профиля.
//   • sync-chain    — длинная цепочка синхронных вызовов (E7.3, по рёбрам графа, не по потокам E3.4);
//   • sync-fan-out  — сервис синхронно зависит от слишком многих (оркестратор-«бог»);
//   • contract-gap  — вызов в код цели, которого нет среди её endpoints (E7.4, часть «маршрут не найден»).
// Честность (adr/0008): берём только уверенные рёбра (≥ MinConfidence) и не Deferred; прочее не утверждаем.
// Лимит находок на вид — MaxPerKind; усечение видно отдельной Coverage-отметкой.
public static class CrossServiceDetectors
{
    public const double MinConfidence = 0.8;
    public const int ChainMinServices = 4;      // 4 сервиса = 3 последовательных синхронных скачка
    public const int FanOutMin = 5;             // ≥5 разных сервисов-зависимостей
    public const int MaxPerKind = 20;
    public const int MaxItems = 50;

    // Шлюзы/BFF по природе зовут многих: их веер не флагуем.
    private static readonly string[] EdgeNames = ["gateway", "bff", "edge", "proxy", "ingress", "aggregator"];

    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph) =>
    [
        .. SyncChains(graph),
        .. FanOut(graph),
        .. ContractGaps(graph),
    ];

    private static Dictionary<string, SystemNode> Services(SystemGraph graph) =>
        graph.Nodes.Where(n => n.Kind == SystemNodeKind.Service)
            .GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    // Уверенные синхронные рёбра между сервисами: (source,target) → максимальная уверенность. Порядок стабилен.
    private static SortedDictionary<(string, string), double> SyncEdges(SystemGraph graph, Dictionary<string, SystemNode> svc)
    {
        var d = new SortedDictionary<(string, string), double>(Comparer<(string, string)>.Create(
            (a, b) => { var c = string.CompareOrdinal(a.Item1, b.Item1); return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2); }));
        foreach (var e in graph.Edges)
            if (e is { Kind: "http-call", Deferred: false } && e.Confidence >= MinConfidence && e.Source != e.Target &&
                svc.ContainsKey(e.Source) && svc.ContainsKey(e.Target))
                d[(e.Source, e.Target)] = d.TryGetValue((e.Source, e.Target), out var c) ? Math.Max(c, e.Confidence) : e.Confidence;
        return d;
    }

    // ── sync-chain ────────────────────────────────────────────────────────────────────────────────────────
    // Циклы отдельно ловит service-cycle: здесь обратные рёбра DFS отбрасываем, получая DAG, и ищем самый длинный путь
    // от каждого «входа» (сервис без входящих синхронных вызовов вообще; чисто циклическая группа входа не имеет — это service-cycle).
    public static IReadOnlyList<SystemProblem> SyncChains(SystemGraph graph)
    {
        var svc = Services(graph);
        var edges = SyncEdges(graph, svc);
        var adj = svc.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (s, t) in edges.Keys) adj[s].Add(t);

        // iterative DFS: убираем обратные рёбра (цикл)
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 — на стеке, 2 — готов
        var back = new HashSet<(string, string)>();
        foreach (var root in svc.Keys.Order(StringComparer.Ordinal))
        {
            if (state.ContainsKey(root)) continue;
            var stack = new Stack<(string V, int I)>();
            stack.Push((root, 0)); state[root] = 1;
            while (stack.Count > 0)
            {
                var (v, i) = stack.Pop();
                if (i < adj[v].Count)
                {
                    stack.Push((v, i + 1));
                    var w = adj[v][i];
                    if (!state.TryGetValue(w, out var st)) { state[w] = 1; stack.Push((w, 0)); }
                    else if (st == 1) back.Add((v, w));
                }
                else state[v] = 2;
            }
        }

        // longest path DP по DAG (мемо, итеративно по постпорядку)
        var next = new Dictionary<string, string?>(StringComparer.Ordinal);
        var len = new Dictionary<string, int>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in svc.Keys.Order(StringComparer.Ordinal))
        {
            var stack = new Stack<(string V, int I)>();
            if (done.Contains(root)) continue;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (v, i) = stack.Pop();
                var outs = adj[v].Where(w => !back.Contains((v, w))).ToList();
                if (i < outs.Count)
                {
                    stack.Push((v, i + 1));
                    if (!done.Contains(outs[i])) stack.Push((outs[i], 0));
                    continue;
                }
                var best = 1; string? bn = null;
                foreach (var w in outs)
                    if (len[w] + 1 > best) { best = len[w] + 1; bn = w; }
                len[v] = best; next[v] = bn; done.Add(v);
            }
        }

        var hasIn = edges.Keys.Select(k => k.Item2).ToHashSet(StringComparer.Ordinal);
        var chains = new List<(List<string> Path, double Conf)>();
        foreach (var entry in svc.Keys.Where(k => !hasIn.Contains(k)).Order(StringComparer.Ordinal))
        {
            if (len[entry] < ChainMinServices) continue;
            var path = new List<string> { entry };
            for (var n = next[entry]; n is not null; n = next[n]) path.Add(n);
            var conf = path.Zip(path.Skip(1), (a, b) => edges[(a, b)]).Min();
            chains.Add((path, conf));
        }

        var ordered = chains.OrderByDescending(c => c.Path.Count).ThenBy(c => c.Path[0], StringComparer.Ordinal).ToList();
        var result = ordered.Take(MaxPerKind).Select(c =>
        {
            var names = c.Path.Select(id => svc[id].Name).ToList();
            return new SystemProblem(ProblemSeverity.Warning, "sync-chain",
                $"синхронная цепочка из {c.Path.Count} сервисов: {string.Join(" → ", names)}",
                c.Path[0], Members: c.Path, Confidence: c.Conf);
        }).ToList();
        return WithTruncation(result, ordered.Count, "sync-chain");
    }

    // ── sync-fan-out ──────────────────────────────────────────────────────────────────────────────────────
    public static IReadOnlyList<SystemProblem> FanOut(SystemGraph graph)
    {
        var svc = Services(graph);
        var edges = SyncEdges(graph, svc);
        var found = edges.GroupBy(e => e.Key.Item1, StringComparer.Ordinal)
            .Where(g => g.Count() >= FanOutMin && !IsEdgeService(svc[g.Key].Name))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var result = found.Take(MaxPerKind).Select(g =>
        {
            var items = g.Take(MaxItems).Select(e => new ProblemItem(svc[e.Key.Item2].Name, "", $"http-call, уверенность {e.Value:0.##}")).ToList();
            return new SystemProblem(ProblemSeverity.Warning, "sync-fan-out",
                $"«{svc[g.Key].Name}» синхронно зависит от {g.Count()} сервисов — упадёт при отказе любого из них",
                g.Key, Items: items, Confidence: g.Min(e => e.Value));
        }).ToList();
        return WithTruncation(result, found.Count, "sync-fan-out");
    }

    private static bool IsEdgeService(string name) =>
        EdgeNames.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

    // ── contract-gap ──────────────────────────────────────────────────────────────────────────────────────
    // Matched=false проставляет HttpCallGraphBuilder только когда endpoints цели известны и маршрут не сошёлся.
    // Статика может не видеть маршрут (minimal API через расширения, прокси-маршруты) → Warning лишь для вызовов с высокой
    // уверенностью, иначе Info. Unknown (Matched=null) не трогаем.
    public static IReadOnlyList<SystemProblem> ContractGaps(SystemGraph graph)
    {
        var svc = Services(graph);
        var found = new List<(SystemEdge Edge, List<HttpCall> Gaps)>();
        foreach (var e in graph.Edges.Where(e => e is { Kind: "http-call", Deferred: false } && svc.ContainsKey(e.Source) && svc.ContainsKey(e.Target)))
        {
            var gaps = (e.Calls ?? []).Where(c => c.Matched == false).ToList();
            if (gaps.Count > 0) found.Add((e, gaps));
        }
        found = [.. found.OrderBy(f => f.Edge.Source, StringComparer.Ordinal).ThenBy(f => f.Edge.Target, StringComparer.Ordinal)];
        var result = found.Take(MaxPerKind).Select(f =>
        {
            var strong = f.Gaps.Max(c => c.Confidence) >= MinConfidence;
            var items = f.Gaps.OrderBy(c => c.Route, StringComparer.Ordinal).ThenBy(c => c.Method, StringComparer.Ordinal).Take(MaxItems)
                .Select(c => new ProblemItem($"{c.Method} {c.Route}", "", c.Evidence ?? "вызов из кода", c.File ?? "", c.Line)).ToList();
            return new SystemProblem(strong ? ProblemSeverity.Warning : ProblemSeverity.Info, "contract-gap",
                $"«{svc[f.Edge.Source].Name}» вызывает {f.Gaps.Count} маршрут(ов), которых нет среди endpoints «{svc[f.Edge.Target].Name}»",
                f.Edge.Source, Items: items, Confidence: f.Gaps.Max(c => c.Confidence));
        }).ToList();
        return WithTruncation(result, found.Count, "contract-gap");
    }

    private static List<SystemProblem> WithTruncation(List<SystemProblem> result, int total, string kind)
    {
        if (total > MaxPerKind)
            result.Add(new SystemProblem(ProblemSeverity.Coverage, $"{kind}-truncated",
                $"показано {MaxPerKind} из {total} находок «{kind}» — остальные скрыты лимитом", ""));
        return result;
    }
}
