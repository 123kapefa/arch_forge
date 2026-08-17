using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core;

// Domain (результат разбора) → контракт графа (IR) для API/фронта.
public static class GraphMapper
{
    public static GraphDto ToDto(AnalysisResult result, string project)
    {
        var idByName = new Dictionary<string, string>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<NodeDto>(result.Types.Count);
        foreach (var t in result.Types)
        {
            // id узла обязан быть уникальным (React-ключ карточки). Обычный случай — «{Слой}:{Имя}»;
            // одноимённые типы в слое (напр. два ApiResponse) дизамбигуируем файлом, затем счётчиком.
            var id = $"{t.Layer}:{t.Name}";
            if (!usedIds.Add(id))
            {
                var withFile = $"{id}@{Path.GetFileNameWithoutExtension(t.File)}";
                id = withFile;
                for (var i = 2; !usedIds.Add(id); i++) id = $"{withFile}#{i}";
            }
            idByName.TryAdd(t.Name, id); // рёбра ссылаются по имени → первый экземпляр (name-based неоднозначность)
            // id члена — тоже React-ключ; перегрузки (два метода с одним именем) дизамбигуируем счётчиком
            var usedMembers = new HashSet<string>(StringComparer.Ordinal);
            var members = t.Members.Count > 0
                ? t.Members.Select(m =>
                {
                    var mid = $"{id}#{m.Name}";
                    for (var i = 2; !usedMembers.Add(mid); i++) mid = $"{id}#{m.Name}#{i}";
                    return new MemberDto(mid, m.Name, m.Kind, m.Signature, m.HttpMethod, m.Route, t.File, m.Line);
                }).ToList()
                : null;
            nodes.Add(new NodeDto(id, t.Name, t.Kind, t.Layer, t.Role, t.Via, t.File, t.Drift, Members: members));
        }

        var edges = new List<EdgeDto>();
        foreach (var e in result.Edges)
            if (idByName.TryGetValue(e.From, out var src) && idByName.TryGetValue(e.To, out var tgt))
                edges.Add(new EdgeDto($"{src}-{e.Kind}->{tgt}", src, tgt, e.Kind));

        var c = result.Coverage;
        return new GraphDto(project, nodes, edges, new CoverageDto(c.Marker, c.Heuristic, c.Unknown, result.Types.Count), result.Drift);
    }
}
