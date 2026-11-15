namespace ArchForge.Analysis.Core;

// Порт: кто владеет правилом формата поля (методичка «Валидация — один раз»). Чисто СИНТАКСИЧЕСКИЙ разбор; реализация — в
// Infrastructure.Roslyn. Отдаёт правила формата в FluentValidation-валидаторах и Value Object-ы со строковой фабрикой.
public interface IValidationSource
{
    ValidationFacts Read(string root, CancellationToken ct = default);
}

/// <param name="Validator">Тип валидатора (<c>AbstractValidator&lt;T&gt;</c>).</param>
/// <param name="Member">Поле, которое проверяет правило (последний сегмент пути лямбды: <c>x.Request.Title</c> → Title).</param>
/// <param name="Checks">Проверки формата в цепочке (MaximumLength, Matches, …).</param>
public sealed record ValidatorRuleSite(string Validator, string Member, IReadOnlyList<string> Checks, string File, int Line, string Layer);

/// <param name="Name">Имя Value Object.</param>
public sealed record ValueObjectSite(string Name, string File, int Line, string Layer);

public sealed record ValidationFacts(IReadOnlyList<ValidatorRuleSite> Rules, IReadOnlyList<ValueObjectSite> ValueObjects)
{
    public static ValidationFacts Empty { get; } = new([], []);
}
