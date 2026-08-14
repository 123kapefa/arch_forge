namespace ArchForge.Analysis.Core.Systems;

// Детектор системного анти-паттерна «общая БД»: в одну РЕЛЯЦИОННУЮ БД пишут ≥2 РАЗНЫХ приложения.
//
// Честность (adr/0008) — что НЕ считаем нарушением:
//  • различаем пишущих по нормализованному образу (api+migrate одного образа = одно приложение);
//  • писателя без образа (build:) не считаем идентичностью — не выдумываем (плата: возможен false negative);
//  • отложенные (Deferred) рёбра не считаем — target ещё не разрешён;
//  • мониторинг-экспортёры (*-exporter) НЕ пишущие приложения — они лишь скрейпят метрики из БД;
//  • только реляционные БД (postgres/mysql/mongo…): общий redis/кэш/объект-стор — обычно осознанный шаринг,
//    не тот анти-паттерн (иначе — лавина ложных срабатываний на реальном таргете).
public static class SharedDbDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph)
    {
        var nodeById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var problems = new List<SystemProblem>();

        foreach (var ds in graph.Nodes.Where(n => n.Kind == SystemNodeKind.Datastore && IsRelationalDb(n.Image)))
        {
            var writers = graph.Edges
                .Where(e => e.Kind == "rw-db" && e.Target == ds.Id && !e.Deferred)
                .Select(e => nodeById.GetValueOrDefault(e.Source))
                .Where(src => src is not null && !IsMonitoring(src.Image))
                .Select(src => NormalizeImage(src!.Image))
                .Where(id => id is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (writers.Count >= 2)
                problems.Add(new SystemProblem(
                    ProblemSeverity.Critical, "shared-db",
                    $"общая БД: в «{ds.Name}» пишут {writers.Count} разных приложения ({string.Join(", ", writers)})",
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

    // Реляционная БД / хранилище данных-«истины» — только для них анти-паттерн общей БД.
    private static bool IsRelationalDb(string? image) =>
        NormalizeImage(image)?.ToLowerInvariant() is "postgres" or "postgresql" or "mysql" or "mariadb" or "mongo" or "mongodb";

    // Мониторинг-экспортёр (postgres-exporter / redis_exporter / node-exporter) — не пишущее приложение.
    private static bool IsMonitoring(string? image)
    {
        var b = NormalizeImage(image);
        return b is not null &&
               (b.EndsWith("-exporter", StringComparison.OrdinalIgnoreCase) ||
                b.EndsWith("_exporter", StringComparison.OrdinalIgnoreCase));
    }
}
