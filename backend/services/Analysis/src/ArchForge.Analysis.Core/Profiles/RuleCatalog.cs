using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Правило каталога (adr/0012). <paramref name="Id"/> совпадает с <c>SystemProblem.Kind</c>, который правило порождает.
/// <paramref name="ByLevel"/> — строгость по умолчанию на уровне; отсутствие записи = «как решил сам детектор» (родная серьёзность),
/// чтобы введение профилей не меняло вывод существующих проектов.</summary>
public sealed record RuleDef(
    string Id,
    string Category,
    string Title,
    string Note,
    IReadOnlyDictionary<ProjectLevel, Strictness> ByLevel)
{
    /// <summary>null — на этом уровне правило работает с родной серьёзностью детектора.</summary>
    public Strictness? DefaultFor(ProjectLevel level) => ByLevel.TryGetValue(level, out var s) ? s : null;
}

/// <summary>Каталог правил, которые умеет проверять анализатор. Снимок со ссылкой на заметки методички
/// (Obsidian, «Правило → уровень»); новые правила добавляются по одному (adr/0012, срез 4).</summary>
public static class RuleCatalog
{
    /// <summary>Версия каталога: пишется в конфигурации, чтобы старая конфигурация не ломалась на новом каталоге.</summary>
    public const string Version = "2026.11";

    private static readonly IReadOnlyDictionary<ProjectLevel, Strictness> None = new Dictionary<ProjectLevel, Strictness>();

    // Мягкие «свод-линзы» (стиль, не архитектура) на простом уровне не проверяются: маркерные интерфейсы там не требуются.
    private static readonly IReadOnlyDictionary<ProjectLevel, Strictness> OffOnSimple =
        new Dictionary<ProjectLevel, Strictness> { [ProjectLevel.Simple] = Strictness.Off };

    public static readonly IReadOnlyList<RuleDef> All =
    [
        new("shared-db", ProblemCategory.Architecture, "Общая БД у нескольких сервисов", "Архитектурная модель", None),
        new("shared-store", ProblemCategory.Architecture, "Общее хранилище у нескольких сервисов", "Архитектурная модель", None),
        new("service-cycle", ProblemCategory.Architecture, "Циклы между сервисами", "Архитектурная модель", None),
        new("dangling-event", ProblemCategory.Architecture, "Событие без подписчика", "Wolverine, Outbox и Inbox", None),
        new("orphaned-subscription", ProblemCategory.Architecture, "Подписка без издателя", "Wolverine, Outbox и Inbox", None),
        new("exposed-datastore", ProblemCategory.Architecture, "Хранилище опубликовано наружу", "Nginx и edge-безопасность", None),
        new("layer-violation", ProblemCategory.Architecture, "Нарушено направление зависимостей слоёв", "Слои Clean Architecture", None),
        new("drift", ProblemCategory.Convention, "Расхождение со сводом (маркеры ролей)", "Вертикальный срез", OffOnSimple),
        new("dto-mapping", ProblemCategory.Convention, "DTO собирается инлайн вне маппера", "Reads и Writes", OffOnSimple),
    ];

    private static readonly Dictionary<string, RuleDef> ById = All.ToDictionary(r => r.Id, StringComparer.Ordinal);

    public static RuleDef? Find(string id) => ById.GetValueOrDefault(id);
}
