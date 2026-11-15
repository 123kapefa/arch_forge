namespace ArchForge.Analysis.Core.Systems;

// Правило «события в агрегате» (методичка «Агрегаты, VO и доменные события»): каждая публичная мутация агрегата поднимает доменное событие
// (RaiseDomainEvent) и обновляет UpdatedAt. Находка уровня сервиса, мягкая (Info, «свод», adr/0008). Чистая функция над IAggregateEventSource.
public static class AggregateEventDetector
{
    public const string Kind = "aggregate-events";
    public const int MaxItems = 1000;

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var sa in analyses)
        {
            var sites = sa.AggregateEvents.Sites.Where(s => !s.Layer.Equals("tests", StringComparison.OrdinalIgnoreCase)).ToList();
            if (sites.Count == 0) continue;
            var items = sites
                .OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line)
                .Take(MaxItems)
                .Select(s => new ProblemItem($"{s.Type}.{s.Member}", s.Layer, Note(s), s.File, s.Line))
                .ToList();
            var noEvent = sites.Count(s => s.Gap.HasFlag(EventGap.NoEvent));
            var noTime = sites.Count(s => s.Gap.HasFlag(EventGap.NoTimestamp));
            var parts = new List<string>();
            if (noEvent > 0) parts.Add($"{noEvent} мутаций без доменного события");
            if (noTime > 0) parts.Add($"{noTime} без обновления времени изменения");
            var more = sites.Count > MaxItems ? $" (показано {MaxItems})" : "";
            problems.Add(new SystemProblem(ProblemSeverity.Info, Kind,
                $"«{sa.Service.Name}»: мутации агрегата без событий — {string.Join(", ", parts)}{more}",
                sa.NodeId ?? sa.Location, ProblemCategory.Convention, items));
        }
        return problems;
    }

    private static string Note(EventSite s) => s.Gap switch
    {
        EventGap.NoEvent | EventGap.NoTimestamp => $"мутация без RaiseDomainEvent и без {s.Timestamp}: поднимите событие с временем (now параметром) и обновите {s.Timestamp}",
        EventGap.NoTimestamp => $"мутация не обновляет {s.Timestamp}, хотя другие методы типа его ставят",
        _ => "мутация без RaiseDomainEvent: поднимите доменное событие (если изменение намеренно тихое — игнорируйте)",
    };
}
