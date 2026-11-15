namespace ArchForge.Analysis.Core;

// Порт: события в агрегате (методичка «Агрегаты, VO и доменные события»: каждая мутация поднимает доменное событие и обновляет UpdatedAt).
// Чисто СИНТАКСИЧЕСКИЙ разбор слоя Domain; реализация — в Infrastructure.Roslyn.
public interface IAggregateEventSource
{
    AggregateEventFacts Read(string root, CancellationToken ct = default);
}

/// <summary>Чего не хватает мутирующему методу.</summary>
[Flags]
public enum EventGap
{
    None = 0,
    NoEvent = 1,        // нет вызова RaiseDomainEvent/AddDomainEvent
    NoTimestamp = 2,    // тип обновляет UpdatedAt в других методах, а в этом — нет
}

/// <param name="Type">Агрегат.</param>
/// <param name="Member">Публичный мутирующий метод.</param>
/// <param name="Gap">Чего нет в методе.</param>
/// <param name="Timestamp">Имя поля времени изменения (<c>UpdatedAt</c>), если речь о нём.</param>
public sealed record EventSite(string Type, string Member, EventGap Gap, string Timestamp, string File, int Line, string Layer);

public sealed record AggregateEventFacts(IReadOnlyList<EventSite> Sites)
{
    public static AggregateEventFacts Empty { get; } = new([]);
}
