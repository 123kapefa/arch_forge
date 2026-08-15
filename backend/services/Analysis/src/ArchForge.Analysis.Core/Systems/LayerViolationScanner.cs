namespace ArchForge.Analysis.Core.Systems;

// Свод-НЕЗАВИСИМЫЙ детектор нарушения направления слоёв — реальная арх-проблема (линза architecture).
// По рёбрам тип-графа: ребро из внутреннего слоя во внешний (Domain→Infrastructure, Application→Api…) = нарушение
// (см. LayerRules). Per-service (через общий ServiceAnalyzer). Включается флагом `/api/system?layers=true`.
public sealed class LayerViolationScanner(ServiceAnalyzer analyzer)
{
    public IReadOnlyList<SystemProblem> Scan(string root, CancellationToken ct = default)
    {
        var problems = new List<SystemProblem>();
        foreach (var (svc, location, result) in analyzer.Analyze(root, ct))
        {
            if (result is null)
            {
                problems.Add(new SystemProblem(ProblemSeverity.Coverage, "layer-scan-failed",
                    $"«{svc.Name}»: не удалось разобрать код для слоёв — пропущен", location, ProblemCategory.Architecture));
                continue;
            }

            // имя типа → слой; НЕОДНОЗНАЧНЫЕ имена (один тип в РАЗНЫХ слоях) исключаем — не судим:
            // ребро адресуется простым именем, «угадать» экземпляр нельзя → детерминизм + без ложных (adr/0008).
            var layerOf = result.Types
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Where(g => g.Select(t => t.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Layer, StringComparer.Ordinal);

            // уникальные пары From→To: несколько видов рёбер (implements+uses) между теми же типами = один факт
            var violations = result.Edges
                .Where(e => layerOf.TryGetValue(e.From, out var fl) && layerOf.TryGetValue(e.To, out var tl)
                            && LayerRules.IsViolation(fl, tl))
                .Select(e => (e.From, e.To))
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
