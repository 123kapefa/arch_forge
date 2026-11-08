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
    IReadOnlyDictionary<ProjectLevel, Strictness> ByLevel,
    RuleExample? Example = null)
{
    /// <summary>null — на этом уровне правило работает с родной серьёзностью детектора.</summary>
    public Strictness? DefaultFor(ProjectLevel level) => ByLevel.TryGetValue(level, out var s) ? s : null;
}

/// <summary>Пример «плохо / хорошо» для интерфейса настройки: строки кода, не привязанные к проекту.</summary>
public sealed record RuleExample(IReadOnlyList<string> Bad, IReadOnlyList<string> Good);

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
        new("shared-db", ProblemCategory.Architecture, "Общая БД у нескольких сервисов", "Архитектурная модель", None, new RuleExample(["orders:   ConnectionStrings__Db=...;Database=main", "billing:  ConnectionStrings__Db=...;Database=main"], ["orders:   ...;Database=orders", "billing:  ...;Database=billing"])),
        new("shared-store", ProblemCategory.Architecture, "Общее хранилище у нескольких сервисов", "Архитектурная модель", None, new RuleExample(["orders:   REDIS=redis:6379/0", "billing:  REDIS=redis:6379/0"], ["orders:   REDIS=redis:6379/0", "billing:  REDIS=redis:6379/1  # свой индекс или свой инстанс"])),
        new("service-cycle", ProblemCategory.Architecture, "Циклы между сервисами", "Архитектурная модель", None, new RuleExample(["orders  -> billing   (http, 0.95)", "billing -> orders    (http, 0.90)   # цикл"], ["orders  -> billing", "billing -> events    # зависимость идёт в одну сторону"])),
        new("dangling-event", ProblemCategory.Architecture, "Событие без подписчика", "Wolverine, Outbox и Inbox", None, new RuleExample(["await bus.Publish(new CourseCreated(id));   // подписчиков нет"], ["await bus.Publish(new CourseCreated(id));   // есть handler: ReindexOnCourseCreated"])),
        new("orphaned-subscription", ProblemCategory.Architecture, "Подписка без издателя", "Wolverine, Outbox и Inbox", None, new RuleExample(["class OnCourseArchived : IHandler<CourseArchived>  // событие никто не публикует"], ["class OnCourseArchived : IHandler<CourseArchived>  // событие публикует CourseService"])),
        new("exposed-datastore", ProblemCategory.Architecture, "Хранилище опубликовано наружу", "Nginx и edge-безопасность", None, new RuleExample(["db:", "  image: postgres:17", "  ports: [\"5432:5432\"]     # опубликован наружу"], ["db:", "  image: postgres:17", "  # порт не публикуется, доступ только из сети compose"])),
        new("layer-violation", ProblemCategory.Architecture, "Нарушено направление зависимостей слоёв", "Слои Clean Architecture", None, new RuleExample(["// Domain.csproj", "<ProjectReference Include=\"..\\Infrastructure\" />"], ["// Domain.csproj", "<!-- ссылок на Infrastructure и Web нет -->"])),
        new("drift", ProblemCategory.Convention, "Расхождение со сводом (маркеры ролей)", "Вертикальный срез", OffOnSimple, new RuleExample(["public record CreateCourse(string Name);            // нет ICommand"], ["public record CreateCourse(string Name) : ICommand;"])),
        new("dto-mapping", ProblemCategory.Convention, "DTO собирается инлайн вне маппера", "Reads и Writes", OffOnSimple, new RuleExample(["// в трёх хендлерах:", "return new UserDto(u.Id, u.Name, u.Email);"], ["// в одном месте:", "return UserMapper.ToDto(u);"])),
    ];

    private static readonly Dictionary<string, RuleDef> ById = All.ToDictionary(r => r.Id, StringComparer.Ordinal);

    public static RuleDef? Find(string id) => ById.GetValueOrDefault(id);
}
