using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

// Общая логика анализа слоёв по тип-графу — единый источник для детектора нарушений (LayerViolationDetector)
// и L0-сводки (ServiceSummaryBuilder), чтобы правила не разъехались (как SystemNamespace для namespace).
public static class LayerGraph
{
    // имя типа → слой; НЕОДНОЗНАЧНЫЕ имена (один тип в РАЗНЫХ слоях) исключаем: ребро адресуется простым
    // именем, экземпляр не угадать → детерминизм + без ложных (adr/0008).
    public static Dictionary<string, string> LayerByName(IReadOnlyList<TypeInfo> types) =>
        types.GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Select(t => t.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Layer, StringComparer.Ordinal);

    // Рёбра-нарушения направления слоёв (внутренний слой From зависит от внешнего To).
    public static IEnumerable<(TypeEdge Edge, string FromLayer, string ToLayer)> Violations(
        IReadOnlyList<TypeEdge> edges, IReadOnlyDictionary<string, string> layerByName)
    {
        foreach (var e in edges)
            if (layerByName.TryGetValue(e.From, out var fl) && layerByName.TryGetValue(e.To, out var tl)
                && LayerRules.IsViolation(fl, tl))
                yield return (e, fl, tl);
    }
}
