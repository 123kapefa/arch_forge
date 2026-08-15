namespace ArchForge.Analysis.Core.Systems;

// Линза «нарушение направления слоёв» (architecture): извлекает нарушения из УЖЕ посчитанного анализа.
// Чистая функция над ServiceAnalysis. В чистой архитектуре зависимости идут ВНУТРЬ; ребро тип-графа из
// внутреннего слоя во внешний (Domain→Infrastructure, Application→Api…) = нарушение (см. LayerRules).
public static class LayerViolationDetector
{
    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var (svc, location, result) in analyses)
        {
            if (result is null)
            {
                problems.Add(new SystemProblem(ProblemSeverity.Coverage, "layer-scan-failed",
                    $"«{svc.Name}»: не удалось разобрать код для слоёв — пропущен", location, ProblemCategory.Architecture));
                continue;
            }

            var layerOf = LayerGraph.LayerByName(result.Types);

            // уникальные пары From→To: несколько видов рёбер (implements+uses) между теми же типами = один факт
            var violations = LayerGraph.Violations(result.Edges, layerOf)
                .Select(v => (v.Edge.From, v.Edge.To))
                .Distinct()
                .ToList();
            if (violations.Count == 0) continue;

            var examples = string.Join("; ", violations.Take(3)
                .Select(v => $"{v.From} ({layerOf[v.From]}) → {v.To} ({layerOf[v.To]})"));
            problems.Add(new SystemProblem(ProblemSeverity.Warning, "layer-violation",
                $"«{svc.Name}»: {violations.Count} нарушений направления слоёв. Напр.: {examples}",
                location, ProblemCategory.Architecture));
        }
        return problems;
    }
}
