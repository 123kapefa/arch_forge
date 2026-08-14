using ArchForge.Contracts;

namespace ArchForge.Map.Core;

/// <summary>
/// Проекции над графом (IR) — «укрощение» перегруженного вида.
/// Чистые функции GraphDto → GraphDto, свободно композируются.
/// Покрытие (Coverage) не пересчитываем: оно честное — по всему анализу, а не по срезу.
/// </summary>
public static class GraphProjections
{
    /// <summary>Фокус: оставить узел и его прямых соседей по рёбрам.</summary>
    public static GraphDto Focus(GraphDto graph, string nodeId)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        foreach (EdgeDto edge in graph.Edges)
        {
            if (edge.Source == nodeId) keep.Add(edge.Target);
            if (edge.Target == nodeId) keep.Add(edge.Source);
        }
        return Restrict(graph, keep);
    }

    /// <summary>Оставить только узлы указанного слоя (и рёбра между ними).</summary>
    public static GraphDto OnlyLayer(GraphDto graph, string layer)
    {
        IEnumerable<string> ids = graph.Nodes
            .Where(n => string.Equals(n.Layer, layer, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id);
        return Restrict(graph, new HashSet<string>(ids, StringComparer.Ordinal));
    }

    /// <summary>Оставить только узлы из keep и рёбра, оба конца которых внутри keep.</summary>
    private static GraphDto Restrict(GraphDto graph, HashSet<string> keep)
    {
        List<NodeDto> nodes = graph.Nodes.Where(n => keep.Contains(n.Id)).ToList();
        List<EdgeDto> edges = graph.Edges
            .Where(e => keep.Contains(e.Source) && keep.Contains(e.Target))
            .ToList();
        return graph with { Nodes = nodes, Edges = edges };
    }
}
