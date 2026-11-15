namespace ArchForge.Analysis.Core;

// Порт: проверки параметров на null в коде сервиса (методичка «Стратегия ошибок и нулей», правило 3). Чисто СИНТАКСИЧЕСКИЙ разбор
// (как RoslynMinimalApiSource); реализация — в Infrastructure.Roslyn. Отдаёт только проверки, где параметр ненулевого типа в файле с
// включённым nullable: там компилятор уже отвечает за нули, и проверка избыточна.
public interface IGuardSource
{
    IReadOnlyList<NullGuardSite> Read(string root, CancellationToken ct = default);
}

/// <param name="Type">Тип, где лежит проверка (ближайший объемлющий).</param>
/// <param name="Member">Метод/конструктор/локальная функция (для первичного конструктора — имя типа).</param>
/// <param name="Param">Имя параметра.</param>
/// <param name="Form"><c>ThrowIfNull</c> | <c>if-throw</c> | <c>??-throw</c>.</param>
public sealed record NullGuardSite(string Type, string Member, string Param, string Form, string File, int Line, string Layer);
