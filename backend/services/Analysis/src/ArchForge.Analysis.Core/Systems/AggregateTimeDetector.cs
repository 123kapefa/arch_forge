namespace ArchForge.Analysis.Core.Systems;

// Правило «даты и аудит живут в агрегате» (методичка «Агрегаты, VO и доменные события»): время приходит в метод агрегата параметром,
// агрегат часов не читает; аудит-поля ставит метод агрегата, а не interceptor/SaveChanges (на одном проекте — один способ).
// Находка уровня сервиса, мягкая (Info, «свод», adr/0008). Чистая функция над IAggregateTimeSource.
public static class AggregateTimeDetector
{
    public const string Kind = "aggregate-time";
    public const int MaxItems = 1000;

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var sa in analyses)
        {
            var clock = sa.AggregateTime.DomainClock.Where(s => !IsTests(s)).ToList();
            var audit = sa.AggregateTime.InterceptorAudit.Where(s => !IsTests(s)).ToList();
            if (clock.Count == 0 && audit.Count == 0) continue;
            var items = clock.Select(s => (Site: s, Note: $"{s.Name} в домене: передайте время параметром (now), источник — TimeProvider в хендлере (если это реализация часов или спецификация — игнорируйте)"))
                .Concat(audit.Select(s => (Site: s, Note: $"{s.Name} ставит interceptor/SaveChanges, а не метод агрегата: перенесите в агрегат")))
                .OrderBy(x => x.Site.File, StringComparer.Ordinal).ThenBy(x => x.Site.Line)
                .Take(MaxItems)
                .Select(x => new ProblemItem($"{x.Site.Type}.{x.Site.Member}", x.Site.Layer, x.Note, x.Site.File, x.Site.Line))
                .ToList();
            var parts = new List<string>();
            if (clock.Count > 0) parts.Add($"{clock.Count} мест читают часы в домене");
            if (audit.Count > 0) parts.Add($"{audit.Count} аудит-полей ставит interceptor/SaveChanges");
            var more = clock.Count + audit.Count > MaxItems ? $" (показано {MaxItems})" : "";
            problems.Add(new SystemProblem(ProblemSeverity.Info, Kind,
                $"«{sa.Service.Name}»: время и аудит не в агрегате — {string.Join(", ", parts)}{more}",
                sa.NodeId ?? sa.Location, ProblemCategory.Convention, items));
        }
        return problems;
    }

    private static bool IsTests(TimeSite s) => s.Layer.Equals("tests", StringComparison.OrdinalIgnoreCase);
}
