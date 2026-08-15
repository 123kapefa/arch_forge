namespace ArchForge.Analysis.Core.Systems;

// Линза «дрейф от свода»: отклонения per-service от свода (маркерные роли) на системный уровень как проблемы
// категории convention, severity Info. Мягко (не красный алерт): на ЧУЖОМ таргете дрейф — это «другие конвенции»,
// а не арх-проблема, поэтому отдельная линза, включаемая по запросу (adr/0008 — не выдаём различия за проблемы).
// Разбор кода — через общий ServiceAnalyzer (гранулярность .sln; моно-репо анализируется целиком).
public sealed class DriftScanner(ServiceAnalyzer analyzer)
{
    public IReadOnlyList<SystemProblem> Scan(string root, CancellationToken ct = default)
    {
        var problems = new List<SystemProblem>();
        foreach (var (svc, location, result) in analyzer.Analyze(root, ct))
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
