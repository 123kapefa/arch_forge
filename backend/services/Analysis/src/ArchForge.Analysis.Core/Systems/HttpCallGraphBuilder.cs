using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// E3.1 срез 1: обогащает системный граф КОНКРЕТНЫМИ http-вызовами из кода (call-graph).
// Для каждого сервиса берём его исходящие вызовы (RoslynHttpCallSource, абсолютные URL), резолвим host → узел цели
// (HttpTargetResolver — так же, как compose/config), сопоставляем маршрут с эндпоинтом цели (RouteMatch, если он
// известен) и вешаем список вызовов на ребро http-call (source→target). Ребра ещё нет → добавляем с провенансом
// code (реальная связь, которой не было в compose). Честность (adr/0008): external/ambiguous/self как ребро не
// заводим; Matched=false остаётся видимым. Запускать ПОСЛЕ дедупа рёбер (сливаем на выживший edge).
//
// Источник вызова = узел АНАЛИЗА: ServiceAnalyzer идёт по пер-сервисному compose, поэтому один анализ = один
// сервис, а его узел — тот, чья система (namespace-префикс id) совпадает с location анализа. Это НЕ зависит от
// классификации слоёв (узел без распознанных слоёв всё равно источник) и от совпадения имени .sln с compose.
public static class HttpCallGraphBuilder
{
    // Уверенность ребра по виду резолва цели (code-провенанс): свой compose — высокая, единственная в чужом — средняя.
    private const double SameSystemConfidence = 0.9;
    private const double InferredConfidence = 0.6;

    public static SystemGraph Enrich(SystemGraph graph, IReadOnlyList<ServiceAnalysis> analyses,
        IReadOnlyDictionary<string, MicroserviceLayerBuilder.Entry>? nodeLayers = null)
    {
        if (analyses.Count == 0) return graph;

        // External-узлы (синтетические "http://host" от нерезолвленных compose/config-URL) НЕ цели резолва: иначе
        // код-вызов на тот же хост схлопнулся бы в Inferred(0.6) вместо честного External (P3 ревью #114).
        var resolver = new HttpTargetResolver(graph.Nodes.Where(n => n.Kind != SystemNodeKind.External));
        // сервис-узлы по системе (namespace-префикс id) — для атрибуции анализа к его узлу.
        var bySystem = graph.Nodes
            .Where(n => n.Kind == SystemNodeKind.Service)
            .GroupBy(n => HttpTargetResolver.SystemOf(n.Id), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // эндпоинты цели для RouteMatch (best-effort): id узла → его эндпоинты из nodeLayers (тот же join, что в ToNodeDto).
        var endpointsById = new Dictionary<string, IReadOnlyList<EndpointDto>>(StringComparer.Ordinal);
        if (nodeLayers is not null)
        {
            var nodeKeyToId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in graph.Nodes)
                if (n.Kind == SystemNodeKind.Service)
                    nodeKeyToId.TryAdd(MicroserviceLayerBuilder.NodeKey(n.Id, n.Name), n.Id);
            foreach (var (key, entry) in nodeLayers)
                if (entry.Endpoints.Count > 0 && nodeKeyToId.TryGetValue(key, out var id))
                    endpointsById[id] = entry.Endpoints;
        }

        // сгруппировать найденные вызовы по (source,target) → список HttpCall; параллельно — Coverage-проблемы
        // о неатрибутируемом (adr/0008: «не гадаем, но и не молчим» — как ResolveHttpCalls в ComposeSystemSource).
        var byEdge = new Dictionary<(string Src, string Tgt), List<HttpCall>>();
        var problems = new List<SystemProblem>();
        var ambiguousSeen = new HashSet<string>(StringComparer.Ordinal); // дедуп «srcId|host» — одна проблема на пару
        foreach (var sa in analyses)
        {
            if (sa.HttpCalls.Count == 0) continue;
            var srcId = SourceNodeId(sa, bySystem);
            if (srcId is null)
            {
                // источник не привязался к узлу (2+ app-узла в системе, тай-брейк не сошёлся) — вызовы есть, но не размещены
                problems.Add(new SystemProblem(ProblemSeverity.Coverage, "unattributed-calls",
                    $"исходящие http-вызовы сервиса «{sa.Service.Name}» не привязаны к узлу системы — карта неполная", ""));
                continue;
            }
            foreach (var hit in sa.HttpCalls)
            {
                if (!Uri.TryCreate(hit.Url, UriKind.Absolute, out var uri)) continue;
                var t = resolver.Resolve(srcId, uri.Host);
                if (t.Kind == HttpTargetKind.Ambiguous)                    // ≥2 одноимённых цели — не выдумываем, но сигналим
                {
                    if (ambiguousSeen.Add($"{srcId}|{uri.Host.ToLowerInvariant()}"))
                        problems.Add(new SystemProblem(ProblemSeverity.Coverage, "ambiguous-target",
                            $"неоднозначная цель http-call «{uri.Host}» из кода — {t.Candidates} одноимённых узла", srcId));
                    continue;
                }
                if (t.NodeId is null) continue;                           // external/self — вне scope среза 1 (не «пробел»)
                var conf = t.Kind == HttpTargetKind.SameSystem ? SameSystemConfidence : InferredConfidence;
                bool? matched = endpointsById.TryGetValue(t.NodeId, out var eps)
                    ? RouteMatch.Matches(hit.Method, uri.AbsolutePath, eps)
                    : null;                                               // эндпоинты цели неизвестны → не сопоставляли
                var call = new HttpCall(hit.Method, uri.AbsolutePath, conf, matched, hit.File, hit.Line, hit.Evidence);
                var k = (srcId, t.NodeId);
                if (!byEdge.TryGetValue(k, out var list)) byEdge[k] = list = [];
                list.Add(call);
            }
        }
        if (byEdge.Count == 0 && problems.Count == 0) return graph;

        // слить на существующее http-call ребро (post-dedup) или добавить новое (провенанс code)
        var edges = graph.Edges.ToList();
        foreach (var (key, calls) in byEdge)
        {
            var ordered = calls
                .OrderBy(c => c.Route, StringComparer.Ordinal)
                .ThenBy(c => c.Method, StringComparer.Ordinal)
                .ToList();
            var i = edges.FindIndex(e => e.Kind == "http-call" && e.Source == key.Src && e.Target == key.Tgt);
            if (i >= 0)
                edges[i] = edges[i] with { Calls = ordered }; // существующее ребро (compose/config) — ДОПОЛНЯЕМ вызовами
            else
                edges.Add(new SystemEdge(key.Src, key.Tgt, "http-call", EdgeVia.Code,
                    ordered.Max(c => c.Confidence), Calls: ordered));
        }
        return graph with
        {
            Edges = edges,
            Problems = problems.Count > 0 ? [.. graph.Problems, .. problems] : graph.Problems,
        };
    }

    // Узел-источник для анализа: сервис-узлы его системы (SystemOf(id) == location). Один → он; иначе снимаем
    // сателлиты (migrate/seed/cron) и берём совпавший по имени с .sln; неоднозначно → null (не гадаем).
    private static string? SourceNodeId(ServiceAnalysis sa, IReadOnlyDictionary<string, List<SystemNode>> bySystem)
    {
        if (!bySystem.TryGetValue(sa.Location, out var cands) || cands.Count == 0) return null;
        if (cands.Count == 1) return cands[0].Id;

        var real = cands.Where(n => !IsSatellite(n.Name)).ToList();
        if (real.Count == 1) return real[0].Id;

        // тай-брейк по имени .sln: точное совпадение, иначе вхождение (auth-service ≈ AuthService), как NameMatchesSln.
        var pool = real.Count > 0 ? real : cands;
        var norm = NormAlnum(sa.Service.Name);
        var exact = pool.Where(n => NormAlnum(n.Name) == norm).ToList();
        if (exact.Count == 1) return exact[0].Id;
        var contains = pool.Where(n => NameOverlaps(NormAlnum(n.Name), norm)).ToList();
        return contains.Count == 1 ? contains[0].Id : null; // неоднозначно → не гадаем
    }

    // Имена «перекрываются» по NormAlnum (одно содержит другое), с порогом длины — иначе короткий общий остаток
    // ложно матчит чужую папку. Тай-брейк, не основной сигнал (как NameMatchesSln в MicroserviceLayerBuilder).
    private static bool NameOverlaps(string a, string b) =>
        a.Length >= 4 && b.Length >= 4 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    // Сателлит-деплой (миграторы/сидеры/крон) — не несёт код сервиса; для тай-брейка источника (как MicroserviceLayerBuilder).
    private static readonly string[] SatelliteMarkers = { "migrat", "seed", "cron" };
    private static bool IsSatellite(string name) => SatelliteMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    // Имя → строчные буквы/цифры: «AuthService» ≡ «auth-service» → «authservice» (тай-брейк источника по имени .sln).
    private static string NormAlnum(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
