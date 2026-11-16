using ArchForge.Contracts;

namespace ArchForge.Map.Core;

/// <summary>
/// E3.4: цепочка вызовов от сервиса-входа по рёбрам системного графа (http-call с вызовами E3.1, publishes/subscribes #61).
/// Чистая функция GraphDto → FlowDto. Уровень — СЕРВИС: граф вызовов не привязывает исходящие вызовы к конкретному
/// action-методу, поэтому показываем все вызовы сервиса (вероятный happy-path, без ветвлений и рантайма) — это в Notes.
/// Честность (adr/0008): нерешённое (маршрут не найден, детали вызова неизвестны, направление шины) не скрываем,
/// цикл/повтор/лимит помечаем статусом, а не молча обрезаем.
/// </summary>
public static class FlowChainBuilder
{
    public const int DefaultMaxDepth = 6, MaxDepthCap = 12;
    public const int DefaultMaxSteps = 150, MaxStepsCap = 500;

    public static FlowDto? Build(GraphDto graph, string entryId, string? endpoint = null, int? maxDepth = null, int? maxSteps = null)
    {
        var nodes = graph.Nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (!nodes.TryGetValue(entryId, out var entry) || entry.Kind != "service") return null;
        var limits = new FlowLimitsDto(Math.Clamp(maxDepth ?? DefaultMaxDepth, 1, MaxDepthCap), Math.Clamp(maxSteps ?? DefaultMaxSteps, 1, MaxStepsCap));

        var outgoing = graph.Edges.GroupBy(e => e.Source, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var subscribers = graph.Edges.Where(e => e.Kind == "subscribes").GroupBy(e => e.Target, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Source, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var steps = new List<FlowStepDto>();
        var notes = new List<string>();
        var expanded = new HashSet<string>(StringComparer.Ordinal);   // сервисы, чьи исходящие уже раскрыты
        var onPath = new HashSet<string>(StringComparer.Ordinal) { entryId };
        var truncated = false;
        var sawAsync = false;
        var capHit = false;

        string? entryLabel = null;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var ep = entry.Endpoints?.FirstOrDefault(x => string.Equals($"{x.Method} {x.Route}", endpoint.Trim(), StringComparison.OrdinalIgnoreCase));
            if (ep is not null) entryLabel = $"{ep.Method} {ep.Route}";
            else notes.Add($"endpoint «{endpoint.Trim()}» не найден у сервиса — поток показан от сервиса целиком");
        }

        expanded.Add(entryId);
        Expand(entryId, parent: null, depth: 1);

        notes.Insert(0, "Реконструировано по коду, без рантайма: вероятный happy-path, ветвления/условия/ретраи не учтены.");
        notes.Insert(1, "Уровень сервиса: вызовы не привязаны к конкретному endpoint — показаны все исходящие вызовы сервиса.");
        if (sawAsync) notes.Add("Подписчики шины определены по шине целиком, без сопоставления имени события.");
        if (!graph.Edges.Any(e => e.Kind == "http-call" && e.Calls is { Count: > 0 }))
            notes.Add("Деталей http-вызовов нет (нужна линза вызовов): хопы показаны на уровне связи сервис→сервис.");
        if (capHit) notes.Add($"Достигнут лимит шагов ({limits.MaxSteps}): цепочка продолжается, но показана не целиком.");
        if (steps.Any(s => s.Status == "truncated")) notes.Add($"Достигнут лимит глубины ({limits.MaxDepth}): цепочка продолжается за ним.");
        if (graph.Degradations is { Count: > 0 }) notes.AddRange(graph.Degradations.Select(d => $"Карта неполная: {d}"));

        return new FlowDto(entryId, entry.Name, steps, truncated || capHit, limits, notes, entryLabel);

        void Expand(string from, int? parent, int depth)
        {
            if (!outgoing.TryGetValue(from, out var edges)) return;
            // порядок детерминирован: синхронные http, затем шина, затем данные; внутри — по цели
            foreach (var e in edges
                .Where(e => e.Kind is "http-call" or "publishes" or "rw-db" or "uses-bus")
                .OrderBy(e => Rank(e.Kind)).ThenBy(e => e.Target, StringComparer.Ordinal))
            {
                if (capHit) return;
                nodes.TryGetValue(e.Target, out var tn);
                var toName = tn?.Name ?? e.Target;
                switch (e.Kind)
                {
                    case "http-call":
                        if (tn?.Kind == "external")
                            Add(from, e.Target, toName, "external", HttpLabel(e), "ok", parent, depth, true, e, note: "внешний вызов — за пределами системы");
                        else
                            Visit(e, tn, "http", HttpLabel(e), true, parent, depth, HttpStatus(e));
                        break;
                    case "publishes":
                        sawAsync = true;
                        var pub = Add(from, e.Target, toName, "publish", $"publish → {toName}", "ok", parent, depth, false, e);
                        if (pub is null) break;
                        var subs = subscribers.GetValueOrDefault(e.Target)?.Where(s => s.Source != from).ToList() ?? [];
                        if (subs.Count == 0) steps[pub.Value] = steps[pub.Value] with { Status = "unresolved", Note = "подписчиков в коде не найдено" };
                        foreach (var s in subs)
                        {
                            if (capHit) return;
                            nodes.TryGetValue(s.Source, out var sn);
                            Visit(s, sn, "consume", $"consume ← {toName}", false, pub, depth + 1, "ok", from: e.Target, target: s.Source);
                        }
                        break;
                    case "rw-db":
                        Add(from, e.Target, toName, "data", "чтение/запись", "ok", parent, depth, null, e);
                        break;
                    case "uses-bus":
                        // направление не определено, если для этой пары нет publishes/subscribes (иначе они уже показаны)
                        if (!edges.Any(x => x.Target == e.Target && x.Kind is "publishes" or "subscribes"))
                            Add(from, e.Target, toName, "bus", "шина", "unresolved", parent, depth, null, e, note: "подключён к шине, направление (publish/subscribe) не определено");
                        break;
                }
            }
        }

        void Visit(EdgeDto e, NodeDto? tn, string kind, string label, bool sync, int? parent, int depth, string status, string? from = null, string? target = null)
        {
            from ??= e.Source;
            target ??= e.Target;
            var toName = tn?.Name ?? target;
            var isService = tn?.Kind == "service";
            if (isService && onPath.Contains(target)) status = "cycle";
            else if (isService && expanded.Contains(target)) status = "repeat";
            else if (isService && depth >= limits.MaxDepth && outgoing.ContainsKey(target)) { status = "truncated"; truncated = true; }
            var idx = Add(from, target, toName, kind, label, status, parent, depth, sync, e);
            if (idx is null || !isService || status is "cycle" or "repeat" or "truncated") return;
            expanded.Add(target);
            onPath.Add(target);
            Expand(target, idx, depth + 1);
            onPath.Remove(target);
        }

        int? Add(string from, string to, string toName, string kind, string label, string status, int? parent, int depth, bool? sync, EdgeDto e, string? note = null)
        {
            if (steps.Count >= limits.MaxSteps) { capHit = true; return null; }
            var calls = e.Kind == "http-call" && e.Calls is { Count: > 0 } ? e.Calls : null;
            steps.Add(new FlowStepDto(steps.Count, depth, from, to, toName, kind, label, status, parent, sync, e.Id, e.Confidence, e.Via, calls, note));
            return steps.Count - 1;
        }
    }

    private static int Rank(string kind) => kind switch { "http-call" => 0, "publishes" => 1, "rw-db" => 2, _ => 3 };

    private static string HttpLabel(EdgeDto e) => e.Calls switch
    {
        { Count: 0 } or null => "http-call",
        { Count: 1 } c => $"{c[0].Method} {c[0].Route}",
        var c => $"{c[0].Method} {c[0].Route} +{c.Count - 1}",
    };

    // есть вызовы, и ни один не сопоставлен с endpoint цели → unmatched; вызовов нет (только связь из compose/config) → unresolved
    private static string HttpStatus(EdgeDto e) =>
        e.Calls is not { Count: > 0 } ? "unresolved"
        : e.Calls.All(c => c.Matched == false) ? "unmatched"
        : "ok";
}
