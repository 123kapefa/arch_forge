namespace ArchForge.Analysis.Core.Systems;

// Детектор общего хранилища: в одно хранилище пишут ≥2 РАЗНЫХ приложения.
//  • реляционная БД / хранилище-«истина» (postgres/mysql/mongo/mssql…) → Critical «общая БД»
//    (жёсткое связывание данных между сервисами);
//  • прочее (redis/minio/кэш) → Warning «общее хранилище» — сигнал связанности НЕ прячем (ревью P1.5-B),
//    но не Critical: общий кэш часто осознан.
//
// Честность (adr/0008) — что НЕ считаем пишущим:
//  • различаем по нормализованному образу (api+migrate одного образа = одно приложение);
//  • писателя без образа (build:) не считаем идентичностью — не выдумываем (возможен false negative);
//  • отложенные (Deferred) рёбра не считаем — target не разрешён;
//  • мониторинг-экспортёры (*-exporter) — не приложения (скрейпят метрики). Компромисс: бизнес-сервис,
//    случайно названный *-exporter, будет пропущен (см. тест) — приоритет чистоте сигнала.
public static class SharedDbDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph)
    {
        var nodeById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var problems = new List<SystemProblem>();

        foreach (var ds in graph.Nodes.Where(n => n.Kind == SystemNodeKind.Datastore))
        {
            var writers = graph.Edges
                .Where(e => e.Kind == "rw-db" && e.Target == ds.Id && !e.Deferred)
                .Select(e => nodeById.GetValueOrDefault(e.Source))
                .Where(src => src is not null && !IsMonitoring(src.Image))
                .Select(src => NormalizeImage(src!.Image))
                .Where(id => id is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (writers.Count < 2) continue;

            var primary = IsPrimaryDb(ds.Image);
            problems.Add(new SystemProblem(
                primary ? ProblemSeverity.Critical : ProblemSeverity.Warning,
                primary ? "shared-db" : "shared-store",
                $"{(primary ? "общая БД" : "общее хранилище")}: в «{ds.Name}» пишут {writers.Count} " +
                    $"разных приложения ({string.Join(", ", writers)})",
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

    // Реляционная БД / хранилище-«истина». NB: MSSQL как mcr.microsoft.com/mssql/server (база «server»)
    // сейчас не классифицируется как Datastore — известный пробел (P1.5 backlog).
    private static bool IsPrimaryDb(string? image) =>
        NormalizeImage(image)?.ToLowerInvariant() is
            "postgres" or "postgresql" or "mysql" or "mariadb" or "mongo" or "mongodb" or "mssql" or "sqlserver";

    private static bool IsMonitoring(string? image)
    {
        var b = NormalizeImage(image);
        return b is not null &&
               (b.EndsWith("-exporter", StringComparison.OrdinalIgnoreCase) ||
                b.EndsWith("_exporter", StringComparison.OrdinalIgnoreCase));
    }
}
