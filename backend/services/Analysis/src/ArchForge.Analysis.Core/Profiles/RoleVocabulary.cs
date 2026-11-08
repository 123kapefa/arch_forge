namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Словарь роли (adr/0012): как эта роль называется в коде проекта. Ключ в профиле — имя роли свода
/// («Command», «Handler», «Validator», «Endpoint», «Aggregate», «DomainEvent»…).</summary>
/// <param name="Markers">Базовые типы/интерфейсы, по которым роль узнаётся уверенно.</param>
/// <param name="Fallback">Запасные признаки (эвристика).</param>
/// <param name="Suffixes">Суффиксы имени типа роли; по ним строится ожидание маркера (дрейф).</param>
public sealed record RoleVocabulary(
    IReadOnlyList<string> Markers,
    IReadOnlyList<string> Fallback,
    IReadOnlyList<string> Suffixes)
{
    /// <summary>Защита от мусора на входе: пустые/слишком длинные имена отбрасываются, записей не больше 64.</summary>
    public RoleVocabulary Sanitized() => new(Clean(Markers), Clean(Fallback), Clean(Suffixes));

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? list) =>
        [.. (list ?? []).Select(s => (s ?? "").Trim()).Where(s => s.Length is > 0 and <= 128)
            .Distinct(StringComparer.Ordinal).Take(64)];
}
