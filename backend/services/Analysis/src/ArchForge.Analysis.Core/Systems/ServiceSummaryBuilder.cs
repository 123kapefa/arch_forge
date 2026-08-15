using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// L0-сводка по найденным сервисам (.sln): карточка со слоями + здоровье каждого слоя (превью L1).
// Агрегирует ТОТ ЖЕ per-service Roslyn-анализ (ServiceAnalysis), что и код-линзы — без второго прохода.
public static class ServiceSummaryBuilder
{
    // Канонический порядок слоёв чистой архитектуры (внешние сверху карточки → внутренние вниз).
    private static readonly string[] LayerOrder = { "API", "Application", "Domain", "Infrastructure", "Contracts" };

    public static IReadOnlyList<ServiceSummaryDto> Build(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var services = new List<ServiceSummaryDto>();
        foreach (var (svc, location, result) in analyses)
        {
            if (result is null) continue; // сбой разбора — сервиса в сводке нет (Coverage-проблема уже поднята линзой)
            var layers = BuildLayers(result);
            if (layers.Count == 0) continue; // не распознали слои — карточку не выдумываем
            services.Add(new ServiceSummaryDto(location, svc.Name, WorstHealth(layers), layers));
        }
        return services;
    }

    private static IReadOnlyList<LayerSummaryDto> BuildLayers(AnalysisResult result)
    {
        // имя типа → слой; неоднозначные (один тип в разных слоях) исключаем (как в LayerViolationDetector)
        var layerOf = result.Types
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Select(t => t.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Layer, StringComparer.Ordinal);

        // слой-источник → внешний слой-цель, если есть нарушение направления
        var violationTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in result.Edges)
            if (layerOf.TryGetValue(e.From, out var fl) && layerOf.TryGetValue(e.To, out var tl) && LayerRules.IsViolation(fl, tl))
                violationTo.TryAdd(fl, tl);

        var layers = new List<LayerSummaryDto>();
        foreach (var g in result.Types
                     .Where(t => LayerRules.LevelOf(t.Layer) is not null) // только судимые слои (не tests/tooling/unknown)
                     .GroupBy(t => t.Layer, StringComparer.OrdinalIgnoreCase))
        {
            var name = Canonical(g.Key);
            var types = g.ToList();
            var driftCount = types.Count(t => t.Drift.Count > 0);
            var unknownRole = types.Count(t => string.Equals(t.Via, "unknown", StringComparison.Ordinal));
            var hasViolation = violationTo.TryGetValue(g.Key, out var toLayer);

            // нарушение слоя / дрейф → warning; только coverage (роль unknown) не портит health, но в заметке
            var health = hasViolation || driftCount > 0 ? "warning" : "ok";
            var note = hasViolation ? $"{name} → {Canonical(toLayer!)} · нарушение слоя"
                : driftCount > 0 ? $"{driftCount} расхождений со сводом"
                : unknownRole > 0 ? $"{unknownRole} тип(ов) без роли"
                : null;

            layers.Add(new LayerSummaryDto(name, health, types.Count, note));
        }

        return layers
            .OrderBy(l => Array.IndexOf(LayerOrder, l.Name) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Canonical(string layer) =>
        LayerOrder.FirstOrDefault(l => string.Equals(l, layer, StringComparison.OrdinalIgnoreCase)) ?? layer;

    private static string WorstHealth(IEnumerable<LayerSummaryDto> layers)
    {
        var set = layers.Select(l => l.Health).ToHashSet(StringComparer.Ordinal);
        return set.Contains("critical") ? "critical" : set.Contains("warning") ? "warning" : "ok";
    }
}
