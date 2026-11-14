namespace ArchForge.Analysis.Core;

// Порт: чем сервис сообщает об ожидаемых ошибках (методичка «Стратегия ошибок и нулей», правило 1: одна стратегия на проект).
// Чисто СИНТАКСИЧЕСКИЙ разбор; реализация — в Infrastructure.Roslyn. Отдаёт два набора мест: методы, возвращающие Result, и
// броски типизированных исключений ожидаемых ошибок (NotFound/Conflict/Validation/…) вне «мостов», где вернуть значение нельзя.
public interface IErrorStrategySource
{
    ErrorStrategyFacts Read(string root, CancellationToken ct = default);
}

/// <param name="Type">Объемлющий тип.</param>
/// <param name="Member">Метод/конструктор/локальная функция.</param>
/// <param name="Name">Для Result — тип результата (<c>Result&lt;…&gt;</c>), для броска — имя исключения.</param>
public sealed record ErrorSite(string Type, string Member, string Name, string File, int Line, string Layer);

public sealed record ErrorStrategyFacts(IReadOnlyList<ErrorSite> ResultReturns, IReadOnlyList<ErrorSite> ExpectedThrows)
{
    public static ErrorStrategyFacts Empty { get; } = new([], []);
}
