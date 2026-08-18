using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core;

// Проекции тип-графа (результат разбора) в контракт GraphDto — экстеншенами по source→DTO.
// Уникальность id (узлов и членов) инкапсулирована в самих проекциях, а не размазана по сборке.
public static class TypeGraphMapping
{
    /// <summary>AnalysisResult (тип-граф) → GraphDto. Рёбра ссылаются на типы по имени → карта имя→id.</summary>
    public static GraphDto ToGraphDto(this AnalysisResult result, string project)
    {
        var idByName = new Dictionary<string, string>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<NodeDto>(result.Types.Count);
        foreach (var t in result.Types)
        {
            var id = UniqueId($"{t.Layer}:{t.Name}", t.File, usedIds);
            idByName.TryAdd(t.Name, id); // рёбра ссылаются по имени → первый экземпляр (name-based неоднозначность)
            nodes.Add(t.ToNodeDto(id));
        }

        var c = result.Coverage;
        return new GraphDto(project, nodes, result.Edges.ToEdgeDtos(idByName),
            new CoverageDto(c.Marker, c.Heuristic, c.Unknown, result.Types.Count), result.Drift);
    }

    /// <summary>Тип → NodeDto (с уже вычисленным уникальным id); члены — с уникальными id (ToMemberDtos).</summary>
    public static NodeDto ToNodeDto(this TypeInfo t, string id) =>
        new(id, t.Name, t.Kind, t.Layer, t.Role, t.Via, t.File, t.Drift, Members: t.Members.ToMemberDtos(id, t.File));

    /// <summary>Члены типа → MemberDto с уникальными id; перегрузки (два «Fail») дизамбигуируются счётчиком. Пусто → null.</summary>
    public static IReadOnlyList<MemberDto>? ToMemberDtos(this IReadOnlyList<MemberInfo> members, string nodeId, string file)
    {
        if (members.Count == 0) return null;
        var used = new HashSet<string>(StringComparer.Ordinal);
        return members.Select(m =>
        {
            var mid = $"{nodeId}#{m.Name}";
            for (var i = 2; !used.Add(mid); i++) mid = $"{nodeId}#{m.Name}#{i}";
            return new MemberDto(mid, m.Name, m.Kind, m.Signature, m.HttpMethod, m.Route, file, m.Line);
        }).ToList();
    }

    /// <summary>Рёбра тип-графа → EdgeDto по карте имя→id; концы, которых нет в карте, пропускаем.</summary>
    public static IReadOnlyList<EdgeDto> ToEdgeDtos(this IReadOnlyList<TypeEdge> edges, IReadOnlyDictionary<string, string> idByName)
    {
        var result = new List<EdgeDto>();
        foreach (var e in edges)
            if (idByName.TryGetValue(e.From, out var src) && idByName.TryGetValue(e.To, out var tgt))
                result.Add(new EdgeDto($"{src}-{e.Kind}->{tgt}", src, tgt, e.Kind));
        return result;
    }

    // id узла обязан быть уникальным (React-ключ карточки). Обычный случай — «{Слой}:{Имя}»;
    // одноимённые типы в слое (напр. два ApiResponse) дизамбигуируем файлом, затем счётчиком.
    private static string UniqueId(string baseId, string file, HashSet<string> used)
    {
        if (used.Add(baseId)) return baseId;
        var withFile = $"{baseId}@{Path.GetFileNameWithoutExtension(file)}";
        var id = withFile;
        for (var i = 2; !used.Add(id); i++) id = $"{withFile}#{i}";
        return id;
    }
}
