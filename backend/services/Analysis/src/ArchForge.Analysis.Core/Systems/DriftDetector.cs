namespace ArchForge.Analysis.Core.Systems;

// Линза «дрейф от свода» (convention): извлекает отклонения из УЖЕ посчитанного per-service анализа.
// Чистая функция над ServiceAnalysis — один проход ServiceAnalyzer питает обе код-линзы (без двойного Roslyn).
// Мягко (Info, не красит health): на чужом таргете дрейф — «другие конвенции», а не арх-проблема (adr/0008).
public static class DriftDetector
{
    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var (svc, location, result) in analyses)
        {
            if (result is null) // сервис не разобрался — не молчим (adr/0008): честная Coverage в той же линзе
            {
                problems.Add(new SystemProblem(ProblemSeverity.Coverage, "drift-scan-failed",
                    $"«{svc.Name}»: не удалось разобрать код для дрейфа — пропущен", location, ProblemCategory.Convention));
                continue;
            }
            if (result.Drift.Count == 0) continue;

            var total = result.Coverage.Marker + result.Coverage.Heuristic + result.Coverage.Unknown;
            var markerPct = total > 0 ? result.Coverage.Marker * 100 / total : 0;
            var examples = string.Join("; ", result.Drift.Take(3));
            problems.Add(new SystemProblem(
                ProblemSeverity.Info, "drift",
                $"«{svc.Name}»: {result.Drift.Count} расхождений со сводом (маркеров {markerPct}%). Напр.: {examples}",
                location, ProblemCategory.Convention));
        }
        return problems;
    }
}
