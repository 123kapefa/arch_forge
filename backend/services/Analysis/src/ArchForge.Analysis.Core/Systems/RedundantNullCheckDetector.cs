namespace ArchForge.Analysis.Core.Systems;

// Правило «лишняя проверка на null» (методичка «Стратегия ошибок и нулей», правило 3): внутри сервиса ненулевой параметр проверяет
// компилятор (Nullable), `ArgumentNullException.ThrowIfNull(p)` на нём избыточен; границу API проверяют один раз в валидаторе.
// Находка уровня сервиса, мягкая (Info, категория «свод»): это стиль и шум, а не архитектурная проблема (adr/0008). Чистая функция
// над уже собранными проверками (IGuardSource), как DriftDetector.
public static class RedundantNullCheckDetector
{
    public const string Kind = "redundant-null-check";

    // Разумный предел на одну находку: перечень в интерфейсе и размер ответа не должны расти без границы.
    public const int MaxItems = 1000;

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var sa in analyses)
        {
            var sites = sa.Guards.Where(g => !g.Layer.Equals("tests", StringComparison.OrdinalIgnoreCase)).ToList();
            if (sites.Count == 0) continue;
            var items = sites
                .OrderBy(g => g.File, StringComparer.Ordinal).ThenBy(g => g.Line)
                .Take(MaxItems)
                .Select(g => new ProblemItem($"{g.Type}.{g.Member}({g.Param})", g.Layer, Note(g.Form), g.File, g.Line))
                .ToList();
            var more = sites.Count > MaxItems ? $" (показано {MaxItems})" : "";
            problems.Add(new SystemProblem(ProblemSeverity.Info, Kind,
                $"«{sa.Service.Name}»: {sites.Count} лишних проверок на null ненулевых параметров{more}",
                sa.NodeId ?? sa.Location, ProblemCategory.Convention, items));
        }
        return problems;
    }

    private static string Note(string form) => form switch
    {
        "ThrowIfNull" => "ThrowIfNull на ненулевом параметре: за нули отвечает Nullable, убрать",
        "if-throw" => "if (… == null) throw ArgumentNullException на ненулевом параметре: убрать",
        "??-throw" => "?? throw ArgumentNullException на ненулевом параметре: убрать",
        _ => "лишняя проверка на null",
    };
}
