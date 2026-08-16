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
            var items = result.Drift.Select(ParseItem).ToList();
            problems.Add(new SystemProblem(
                ProblemSeverity.Info, "drift",
                $"«{svc.Name}»: {result.Drift.Count} расхождений со сводом (маркеров {markerPct}%)",
                location, ProblemCategory.Convention, items));
        }
        return problems;
    }

    // «Name (Layer): note» → структурный факт (для раскрытия списком на фронте).
    private static ProblemItem ParseItem(string s)
    {
        var open = s.LastIndexOf(" (", StringComparison.Ordinal);
        var close = open >= 0 ? s.IndexOf("): ", open, StringComparison.Ordinal) : -1;
        return open > 0 && close > open
            ? new ProblemItem(s[..open], s[(open + 2)..close], s[(close + 3)..])
            : new ProblemItem(s, "", "");
    }
}
