namespace ArchForge.Analysis.Core.Systems;

// Правило «смесь стратегий ошибок» (методичка «Стратегия ошибок и нулей», правило 1): в одном сервисе ожидаемые ошибки либо
// возвращают (Result), либо бросают (NotFoundException/…), но не то и другое. Находка уровня сервиса, мягкая (Info, «свод», adr/0008).
// В список выносится МЕНЬШИНСТВО (отклонение от преобладающей стратегии; при равенстве — броски). Чистая функция над IErrorStrategySource.
public static class MixedErrorStrategyDetector
{
    public const string Kind = "mixed-error-strategy";
    public const int MaxItems = 1000;

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var sa in analyses)
        {
            var results = sa.ErrorStrategy.ResultReturns.Where(s => !IsTests(s)).ToList();
            var throws = sa.ErrorStrategy.ExpectedThrows.Where(s => !IsTests(s)).ToList();
            if (results.Count == 0 || throws.Count == 0) continue;
            var minorityIsResult = results.Count < throws.Count;
            var minority = minorityIsResult ? results : throws;
            var items = minority
                .OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line)
                .Take(MaxItems)
                .Select(s => new ProblemItem($"{s.Type}.{s.Member}", s.Layer,
                    minorityIsResult ? $"возвращает {s.Name}, а в сервисе ожидаемые ошибки в основном бросают" : $"бросает {s.Name}, а в сервисе ожидаемые ошибки в основном возвращают Result",
                    s.File, s.Line))
                .ToList();
            problems.Add(new SystemProblem(ProblemSeverity.Info, Kind,
                $"«{sa.Service.Name}»: смесь стратегий ошибок — {results.Count} методов возвращают Result, {throws.Count} методов бросают исключения ожидаемых ошибок",
                sa.NodeId ?? sa.Location, ProblemCategory.Convention, items));
        }
        return problems;
    }

    private static bool IsTests(ErrorSite s) => s.Layer.Equals("tests", StringComparison.OrdinalIgnoreCase);
}
