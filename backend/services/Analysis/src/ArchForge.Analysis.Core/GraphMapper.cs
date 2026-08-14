using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core;

// Domain (результат разбора) → контракт графа (IR) для API/фронта.
public static class GraphMapper
{
    public static GraphDto ToDto(AnalysisResult result, string project)
    {
        var idByName = new Dictionary<string, string>();
        var nodes = new List<NodeDto>(result.Types.Count);
        foreach (var t in result.Types)
        {
            var id = $"{t.Layer}:{t.Name}";
            idByName.TryAdd(t.Name, id);
            nodes.Add(new NodeDto(id, t.Name, t.Kind, t.Layer, t.Role, t.Via, t.File, t.Drift));
        }

        var edges = new List<EdgeDto>();
        foreach (var e in result.Edges)
            if (idByName.TryGetValue(e.From, out var src) && idByName.TryGetValue(e.To, out var tgt))
                edges.Add(new EdgeDto($"{src}-{e.Kind}->{tgt}", src, tgt, e.Kind));

        var c = result.Coverage;
        return new GraphDto(project, nodes, edges, new CoverageDto(c.Marker, c.Heuristic, c.Unknown, result.Types.Count), result.Drift);
    }
}
