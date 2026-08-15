namespace ArchForge.Analysis.Core.Systems;

// Детектор системного анти-паттерна «цикл сервисов»: взаимно достижимые сервисы по http-call
// (нет чёткого направления зависимостей). SCC (алгоритм Тарьяна) по подграфу http-call между Service-узлами;
// компонента размером ≥2 = цикл. Честность (adr/0008): только уверенные (не Deferred) рёбра к своим сервисам.
public static class ServiceCycleDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph)
    {
        var services = graph.Nodes.Where(n => n.Kind == SystemNodeKind.Service)
            .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var nameById = graph.Nodes.ToDictionary(n => n.Id, n => n.Name, StringComparer.Ordinal);

        var adj = services.ToDictionary(s => s, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in graph.Edges)
            if (e is { Kind: "http-call", Deferred: false } &&
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

    // Компактный Тарьян: сильно связные компоненты ориентированного графа. Графы сервисов малы (рекурсия ок).
    private static List<List<string>> Tarjan(Dictionary<string, List<string>> adj)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;

        void Connect(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in adj[v])
            {
                if (!index.ContainsKey(w)) { Connect(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
            }

            if (low[v] == index[v])
            {
                var scc = new List<string>();
                string w;
                do { w = stack.Pop(); onStack.Remove(w); scc.Add(w); } while (w != v);
                result.Add(scc);
            }
        }

        foreach (var v in adj.Keys)
            if (!index.ContainsKey(v)) Connect(v);

        return result;
    }
}
