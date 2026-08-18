namespace ArchForge.Analysis.Core.Systems;

// Детектор системного анти-паттерна «цикл сервисов»: взаимно достижимые сервисы по http-call
// (нет чёткого направления зависимостей). SCC (алгоритм Тарьяна) по подграфу http-call между Service-узлами;
// компонента размером ≥2 = цикл.
public static class ServiceCycleDetector
{
    // Цикл образуют только УВЕРЕННО разрешённые вызовы (цель в том же compose, conf 0.8). Догадки о цели по
    // совпадению имени между несвязанными системами (0.5) не эскалируем в Critical — ложная уверенность хуже
    // честного unknown (adr/0008). Межсистемные циклы — отдельный срез с меньшей серьёзностью (backlog).
    private const double MinConfidence = 0.8;

    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph)
    {
        var services = graph.Nodes.Where(n => n.Kind == SystemNodeKind.Service)
            .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        // безопасный build (дубли Id → берём первый, а не падаем — честная деградация)
        var nameById = graph.Nodes
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var adj = services.ToDictionary(s => s, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in graph.Edges)
            if (e is { Kind: "http-call", Deferred: false } && e.Confidence >= MinConfidence &&
                e.Source != e.Target && services.Contains(e.Source) && services.Contains(e.Target))
                adj[e.Source].Add(e.Target);

        var problems = new List<SystemProblem>();
        foreach (var scc in Tarjan(adj).Where(c => c.Count >= 2))
        {
            var ordered = scc.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var names = ordered.Select(id => nameById.GetValueOrDefault(id, id));
            problems.Add(new SystemProblem(
                ProblemSeverity.Critical, "service-cycle",
                $"цикл сервисов ({scc.Count}): {string.Join(" ↔ ", names)}",
                ordered[0])); // локализуем на узел (health узла подсветится, ревью P1.5-E M3)
        }
        return problems;
    }

    // Тарьян: сильно связные компоненты ориентированного графа. ИТЕРАТИВНО (явный стек кадров вместо
    // рекурсии) — большой граф вызовов с длинной цепочкой не должен ронять поток StackOverflow'ом (P1.5-H).
    private static List<List<string>> Tarjan(Dictionary<string, List<string>> adj)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var comp = new Stack<string>();                    // Тарьян-стек текущей компоненты
        var result = new List<List<string>>();
        var counter = 0;

        var work = new Stack<(string V, int I)>();          // кадр «рекурсии»: узел + индекс следующего соседа
        foreach (var root in adj.Keys)
        {
            if (index.ContainsKey(root)) continue;
            work.Push((root, 0));

            while (work.Count > 0)
            {
                var (v, i) = work.Pop();
                if (i == 0) { index[v] = low[v] = counter++; comp.Push(v); onStack.Add(v); } // первый вход в v

                var neighbors = adj[v];
                var descended = false;
                while (i < neighbors.Count)
                {
                    var w = neighbors[i];
                    if (!index.ContainsKey(w))
                    {
                        work.Push((v, i + 1));              // продолжим v после возврата из w
                        work.Push((w, 0));                 // «уходим» в w
                        descended = true;
                        break;
                    }
                    if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                    i++;
                }
                if (descended) continue;

                if (low[v] == index[v]) // корень SCC — вынимаем компоненту
                {
                    var scc = new List<string>();
                    string w;
                    do { w = comp.Pop(); onStack.Remove(w); scc.Add(w); } while (w != v);
                    result.Add(scc);
                }
                if (work.Count > 0) // «возврат» в родителя: подтягиваем его low по завершённому v
                {
                    var parent = work.Peek().V;
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }
        }
        return result;
    }
}
