namespace ArchForge.Analysis.Core.Systems;

// Резолв цели http-call: host → узел системы (по имени). Вынесен из ComposeSystemSource (E3.1 срез 0),
// чтобы одинаково резолвить и compose/config-URL, и абсолютные URL из кода (HttpCallGraphBuilder).
// Возвращает ВИД резолва (без уверенности — её мапит вызывающий: config-провенанс и code-провенанс разные).
// Честность (adr/0008): DNS compose локален — цель в СВОЁМ compose достоверна; в чужом — inferred;
// ≥2 одноимённых — ambiguous (не выдумываем); нет узла — external.
public enum HttpTargetKind { SameSystem, Inferred, External, Ambiguous, SelfOnly }

public readonly record struct HttpTarget(HttpTargetKind Kind, string? NodeId, int Candidates);

public sealed class HttpTargetResolver
{
    private readonly Dictionary<string, List<string>> _byName;

    public HttpTargetResolver(IEnumerable<SystemNode> nodes) =>
        _byName = nodes
            .GroupBy(n => n.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList(), StringComparer.Ordinal);

    public HttpTarget Resolve(string sourceId, string host)
    {
        if (!_byName.TryGetValue(host.ToLowerInvariant(), out var ids))
            return new(HttpTargetKind.External, null, 0);

        var others = ids.Where(id => id != sourceId).ToList();
        if (others.Count == 0) return new(HttpTargetKind.SelfOnly, null, 0); // только сам себя → self-call

        var sameSystem = others.Where(id => SystemOf(id) == SystemOf(sourceId)).ToList();
        if (sameSystem.Count == 1) return new(HttpTargetKind.SameSystem, sameSystem[0], 1); // DNS того же compose
        if (sameSystem.Count == 0 && others.Count == 1) return new(HttpTargetKind.Inferred, others[0], 1); // единственная в чужом
        return new(HttpTargetKind.Ambiguous, null, sameSystem.Count > 1 ? sameSystem.Count : others.Count);
    }

    // «Система» узла = namespace-префикс id ({system}/{svc}); внешние/инфра-id без '/' → пустая строка.
    public static string SystemOf(string id)
    {
        var i = id.LastIndexOf('/');
        return i > 0 ? id[..i] : "";
    }
}
