using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// L0-сводка по найденным сервисам (.sln): карточка со слоями + здоровье каждого слоя (превью L1).
// Агрегирует ТОТ ЖЕ per-service Roslyn-анализ (ServiceAnalysis), что и код-линзы — без второго прохода.
// Ограничение: карточка только у сервисов С compose (ServiceAnalyzer фильтрует их — у остальных нет узлов
// в системном графе, к которым привязать карточку); .sln-библиотека без compose в сводку не попадает.
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
        var layerOf = LayerGraph.LayerByName(result.Types);

        // слой-источник → внешний слой-цель нарушения; берём МИНИМАЛЬНЫЙ по Ordinal — детерминизм заметки
        var violationTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, fl, tl) in LayerGraph.Violations(result.Edges, layerOf))
            if (!violationTo.TryGetValue(fl, out var cur) || string.CompareOrdinal(tl, cur) < 0)
                violationTo[fl] = tl;

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

            // ТОЛЬКО нарушение направления слоя красит health (реальная арх-проблема). Дрейф и роль-unknown —
            // конвенции/покрытие: показываем в заметке, но health НЕ портим (adr/0008, инвариант линз).
            var health = hasViolation ? "warning" : "ok";
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
