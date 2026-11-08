namespace ArchForge.Analysis.Core.Systems;

// Детектор общего хранилища: в одно хранилище пишут ≥2 РАЗНЫХ приложения.
//  • реляционная БД / хранилище-«истина» (postgres/mysql/mongo/mssql…) → Critical «общая БД»
//    (жёсткое связывание данных между сервисами);
//  • прочее (redis/minio/кэш) → Warning «общее хранилище» — сигнал связанности НЕ прячем (ревью P1.5-B),
//    но не Critical: общий кэш часто осознан.
//
// Честность (adr/0008) — что НЕ считаем пишущим:
//  • различаем по нормализованному образу (api+migrate одного образа = одно приложение);
//  • писателя без образа (build:) различаем по СИСТЕМЕ + базовому имени сервиса (без суффиксов миграций/типа) — G3:
//    иначе на build-монорепо (все сервисы build:) shared-db никогда не срабатывал бы; при этом «svc»+«svc-migrations»
//    и «auth-api»+«auth-migrate» остаются ОДНИМ приложением, разные сервисы — разными, а одноимённые из РАЗНЫХ
//    систем не сливаются;
//  • отложенные (Deferred) рёбра не считаем — target не разрешён;
//  • мониторинг-экспортёры (*-exporter) — не приложения (скрейпят метрики). Компромисс: бизнес-сервис,
//    случайно названный *-exporter, будет пропущен (см. тест) — приоритет чистоте сигнала.
public static class SharedDbDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph, Profiles.ServiceNaming? naming = null)
    {
        naming ??= Profiles.ServiceNaming.Default;
        var nodeById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var problems = new List<SystemProblem>();

        foreach (var ds in graph.Nodes.Where(n => n.Kind == SystemNodeKind.Datastore))
        {
            var writers = graph.Edges
                .Where(e => e.Kind == "rw-db" && e.Target == ds.Id && !e.Deferred)
                .Select(e => nodeById.GetValueOrDefault(e.Source))
                .Where(src => src is not null && !IsMonitoring(src.Image) && !IsMigrationRunner(src))
                .GroupBy(src => WriterIdentity(src!, naming), StringComparer.OrdinalIgnoreCase)  // одно приложение = одна идентичность
                .Select(g => g.First()!)                                                  // узел-представитель для отображения
                .ToList();

            if (writers.Count < 2) continue;

            var primary = IsPrimaryDb(ds.Image);
            problems.Add(new SystemProblem(
                primary ? ProblemSeverity.Critical : ProblemSeverity.Warning,
                primary ? "shared-db" : "shared-store",
                $"{(primary ? "общая БД" : "общее хранилище")}: в «{ds.Name}» пишут {writers.Count} " +
                    $"разных приложения ({string.Join(", ", writers.Select(w => w.Name))})",
                ds.Id));
        }

        return problems;
    }

    // «ghcr.io/ns/kalmykov-auth-api:stage» → «kalmykov-auth-api» (без реестра и тега).
    // NB: реестр с портом (host:5000/img) сломал бы split по ':' — в наших образах не встречается (P1.5 backlog).
    internal static string? NormalizeImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var last = image.Split('/')[^1].Split(':', 2)[0];
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    // Идентичность «пишущего приложения»: по образу (api+migrate одного образа = одно приложение);
    // build-сервис без образа — по базовому имени сервиса (без суффикса миграций), с префиксом «name:»,
    // чтобы имя-идентичность не столкнулась с образом-идентичностью.
    // Списки суффиксов — в Profiles.ServiceNaming (одно место, настраиваются профилем).

    // Идентичность «пишущего приложения»:
    //  • по образу — общий образ = одно приложение (api+migrate одного образа сходятся);
    //  • build-сервис без образа — по СИСТЕМЕ (namespace id «{system}/{svc}») + базовому имени (без суффиксов
    //    миграций И типа). Namespace: одноимённые сервисы в РАЗНЫХ системах (sysA/api vs sysB/api) не сливаются.
    //    Двойное снятие суффикса: «auth-api» и «auth-migrate» сходятся в общую базу «auth».
    private static string WriterIdentity(SystemNode n, Profiles.ServiceNaming naming) =>
        NormalizeImage(n.Image) is { } img ? img : $"name:{SystemOf(n.Id)}:{AppBaseName(n.Name, naming)}";

    // Система узла = namespace-префикс id ({system}/{svc}); внешние/инфра-id без '/' → пустая строка.
    private static string SystemOf(string id)
    {
        var i = id.LastIndexOf('/');
        return i > 0 ? id[..i] : "";
    }

    private static string AppBaseName(string name, Profiles.ServiceNaming naming)
    {
        var b = (name ?? "").Trim();
        foreach (var s in naming.MigrationSuffixes)
            if (b.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { b = b[..^s.Length]; break; }
        foreach (var s in naming.TypeSuffixes)
            if (b.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { b = b[..^s.Length]; break; }
        return b.ToLowerInvariant();
    }

    // Отдельный мигратор (имя целиком — «migrate»/«flyway»/…) — не самостоятельное приложение, а часть чужого:
    // исключаем из писателей (как *-exporter). Именованные «<svc>-migrations» фолдятся в «<svc>» через AppBaseName.
    private static readonly string[] MigrationNames =
        { "migrate", "migration", "migrations", "db-migrate", "dbmigrate", "flyway", "liquibase" };
    private static bool IsMigrationRunner(SystemNode n) =>
        MigrationNames.Contains(n.Name.Trim().ToLowerInvariant());

    // Реляционная БД / хранилище-«истина». pgvector — postgres-вариант (G1). NB: MSSQL как
    // mcr.microsoft.com/mssql/server (база «server») сейчас не классифицируется как Datastore — backlog.
    private static bool IsPrimaryDb(string? image) =>
        NormalizeImage(image)?.ToLowerInvariant() is
            "postgres" or "postgresql" or "pgvector" or "mysql" or "mariadb" or "mongo" or "mongodb" or "mssql" or "sqlserver";

    private static bool IsMonitoring(string? image)
    {
        var b = NormalizeImage(image);
        return b is not null &&
               (b.EndsWith("-exporter", StringComparison.OrdinalIgnoreCase) ||
                b.EndsWith("_exporter", StringComparison.OrdinalIgnoreCase));
    }
}
