namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Как называются сервисы в compose проекта (adr/0012): суффиксы типа сервиса, суффиксы миграторов, маркеры сателлитов.
/// Раньше списки были продублированы в нескольких детекторах; теперь одно место с прежними значениями по умолчанию.</summary>
/// <param name="TypeSuffixes">«-api», «-service»…: снимаются при сведении «auth-api» и «auth-migrate» к одному приложению.</param>
/// <param name="MigrationSuffixes">«-migrate»…: снимаются раньше суффикса типа.</param>
/// <param name="SatelliteMarkers">Подстроки имён деплоев-сателлитов (миграторы, сидеры, крон), которые не несут код сервиса.
/// «worker» намеренно не входит: это легитимный тип сервиса.</param>
public sealed record ServiceNaming(
    IReadOnlyList<string> TypeSuffixes,
    IReadOnlyList<string> MigrationSuffixes,
    IReadOnlyList<string> SatelliteMarkers)
{
    public static ServiceNaming Default { get; } = new(
        ["-service", "-svc", "-api", "-worker", "-gateway", "-app"],
        ["-migrations", "-migration", "-migrate"],
        ["migrat", "seed", "cron"]);

    /// <summary>Суффиксы для ключей слоёв микросервисов (catalog-api → catalog). Не настраиваются профилем: ключ должен
    /// совпадать между сборкой слоёв и сопоставлением карточек, а источник compose-узлов работает раньше профиля.</summary>
    public static readonly IReadOnlyList<string> LayerKeySuffixes = ["-api", "-service", "-ui", "-app", "-web", "-server"];

    public bool IsDefault =>
        TypeSuffixes.SequenceEqual(Default.TypeSuffixes) &&
        MigrationSuffixes.SequenceEqual(Default.MigrationSuffixes) &&
        SatelliteMarkers.SequenceEqual(Default.SatelliteMarkers);

    public bool IsSatellite(string name) =>
        SatelliteMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Защита от мусора на входе: пустые и слишком длинные строки отбрасываются, число записей ограничено;
    /// пустой список → значение по умолчанию (иначе, например, пустые маркеры сателлитов молча изменили бы поведение).</summary>
    public ServiceNaming Sanitized() => new(
        Clean(TypeSuffixes, Default.TypeSuffixes),
        Clean(MigrationSuffixes, Default.MigrationSuffixes),
        Clean(SatelliteMarkers, Default.SatelliteMarkers));

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? list, IReadOnlyList<string> fallback)
    {
        var cleaned = (list ?? [])
            .Select(s => (s ?? "").Trim())
            .Where(s => s.Length is > 0 and <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        return cleaned.Count > 0 ? cleaned : fallback;
    }
}
