using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Сводка слоёв: канонический порядок + здоровье каждого слоя (превью для L0/L1).
// `Layers` переиспользуется per-.sln (ServiceSummaryBuilder) и per-microservice (MicroserviceLayerBuilder).
public static class ServiceSummaryBuilder
{
    private static readonly string[] LayerOrder = { "API", "Application", "Domain", "Infrastructure", "Contracts" };

    public static IReadOnlyList<ServiceSummaryDto> Build(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var services = new List<ServiceSummaryDto>();
        foreach (var (svc, location, result) in analyses)
        {
            if (result is null) continue; // сбой разбора — сервиса в сводке нет (Coverage уже поднята линзой)
            var layers = Layers(result.Types, result.Edges);
            if (layers.Count == 0) continue;
            services.Add(new ServiceSummaryDto(location, svc.Name, WorstHealth(layers), layers));
        }
        return services;
    }

    /// <summary>Сводка слоёв по набору типов и рёбер тип-графа (для .sln целиком или одного микросервиса).</summary>
    public static IReadOnlyList<LayerSummaryDto> Layers(IReadOnlyList<TypeInfo> types, IReadOnlyList<TypeEdge> edges)
    {
        var layerOf = LayerGraph.LayerByName(types);

        // слой-источник → внешний слой-цель нарушения; минимальный по Ordinal — детерминизм заметки
        var violationTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, fl, tl) in LayerGraph.Violations(edges, layerOf))
            if (!violationTo.TryGetValue(fl, out var cur) || string.CompareOrdinal(tl, cur) < 0)
                violationTo[fl] = tl;

        var layers = new List<LayerSummaryDto>();
        foreach (var g in types
                     .Where(t => LayerRules.LevelOf(t.Layer) is not null) // только судимые слои (не tests/tooling/unknown)
                     .GroupBy(t => t.Layer, StringComparer.OrdinalIgnoreCase))
        {
            var name = Canonical(g.Key);
            var group = g.ToList();
            var driftCount = group.Count(t => t.Drift.Count > 0);
            var unknownRole = group.Count(t => string.Equals(t.Via, "unknown", StringComparison.Ordinal));
            var hasViolation = violationTo.TryGetValue(g.Key, out var toLayer);

            // ТОЛЬКО нарушение слоя красит health; дрейф и роль-unknown — в заметке (инвариант линз, adr/0008)
            var health = hasViolation ? "warning" : "ok";
            var note = hasViolation ? $"{name} → {Canonical(toLayer!)} · нарушение слоя"
                : driftCount > 0 ? $"{driftCount} расхождений со сводом"
                : unknownRole > 0 ? $"{unknownRole} тип(ов) без роли"
                : null;

            layers.Add(new LayerSummaryDto(name, health, group.Count, note));
        }

        return layers
            .OrderBy(l => Array.IndexOf(LayerOrder, l.Name) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static string WorstHealth(IEnumerable<LayerSummaryDto> layers)
    {
        var set = layers.Select(l => l.Health).ToHashSet(StringComparer.Ordinal);
        return set.Contains("critical") ? "critical" : set.Contains("warning") ? "warning" : "ok";
    }

    private static string Canonical(string layer) =>
        LayerOrder.FirstOrDefault(l => string.Equals(l, layer, StringComparison.OrdinalIgnoreCase)) ?? layer;
}
