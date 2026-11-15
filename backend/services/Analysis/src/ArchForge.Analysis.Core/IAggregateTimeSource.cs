namespace ArchForge.Analysis.Core;

// Порт: кто ставит даты и аудит (методичка «Агрегаты, VO и доменные события», правило «даты, события и аудит живут в агрегате»).
// Чисто СИНТАКСИЧЕСКИЙ разбор; реализация — в Infrastructure.Roslyn. Два набора мест:
// часы в домене (DateTime.UtcNow внутри слоя Domain — время должно приходить параметром) и аудит-поля, которые ставит
// SaveChanges/interceptor, а не метод агрегата.
public interface IAggregateTimeSource
{
    AggregateTimeFacts Read(string root, CancellationToken ct = default);
}

/// <param name="Type">Объемлющий тип.</param>
/// <param name="Member">Метод/конструктор/свойство, где стоит место.</param>
/// <param name="Name">Для часов — <c>DateTime.UtcNow</c> и т. п.; для аудита — имя поля (<c>UpdatedAt</c>).</param>
public sealed record TimeSite(string Type, string Member, string Name, string File, int Line, string Layer);

public sealed record AggregateTimeFacts(IReadOnlyList<TimeSite> DomainClock, IReadOnlyList<TimeSite> InterceptorAudit)
{
    public static AggregateTimeFacts Empty { get; } = new([], []);
}
