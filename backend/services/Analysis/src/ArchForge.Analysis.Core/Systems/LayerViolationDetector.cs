namespace ArchForge.Analysis.Core.Systems;

// Линза «нарушение направления слоёв» (architecture): извлекает нарушения из УЖЕ посчитанного анализа.
// Чистая функция над ServiceAnalysis. В чистой архитектуре зависимости идут ВНУТРЬ; ребро тип-графа из
// внутреннего слоя во внешний (Domain→Infrastructure, Application→Api…) = нарушение (см. LayerRules).
public static class LayerViolationDetector
{
    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var (svc, location, result) in analyses.ForProblems())
        {
            if (result is null)
            {
                problems.Add(new SystemProblem(ProblemSeverity.Coverage, "layer-scan-failed",
                    $"«{svc.Name}»: не удалось разобрать код для слоёв — пропущен", location, ProblemCategory.Architecture));
                continue;
            }

            var layerOf = LayerGraph.LayerByName(result.Types);
            // имя типа → файл (для перехода к блоку-источнику нарушения); при коллизии имён берём первый
            var fileOf = result.Types
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().File, StringComparer.Ordinal);

            // уникальные пары From→To: несколько видов рёбер (implements+uses) между теми же типами = один факт
            var violations = LayerGraph.Violations(result.Edges, layerOf)
                .Select(v => (v.Edge.From, v.Edge.To))
                .Distinct()
                .ToList();
            if (violations.Count == 0) continue;

            var items = violations
                .Select(v => new ProblemItem($"{v.From} → {v.To}", $"{layerOf[v.From]} → {layerOf[v.To]}",
                    "зависимость наружу", fileOf.TryGetValue(v.From, out var f) ? f : ""))
                .ToList();
            problems.Add(new SystemProblem(ProblemSeverity.Warning, "layer-violation",
                $"«{svc.Name}»: {violations.Count} нарушений направления слоёв",
                location, ProblemCategory.Architecture, items));
        }
        return problems;
    }
}
