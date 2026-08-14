namespace ArchForge.Analysis.Core.Systems;

// Детектор системного анти-паттерна «общая БД»: в одно хранилище пишут ≥2 РАЗНЫХ приложения.
// Различаем пишущих по НОРМАЛИЗОВАННОМУ образу (без реестра/тега), а не по контейнеру —
// иначе api + migrate одного сервиса (один образ, разные контейнеры) дали бы ложную «общую БД».
// Писателя без образа (build:-сервис) НЕ считаем отдельной идентичностью — иначе api+migrate через build
// снова дали бы ложную находку. Плата за честность: общую БД между двумя build-only сервисами можем
// пропустить (false negative), но не выдумываем (false positive) — приоритет чистоты сигнала (adr/0008).
// Отложенные (Deferred) рёбра не считаем — target ещё не разрешён.
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
                .Select(e => nodeById.TryGetValue(e.Source, out var src) ? NormalizeImage(src.Image) : null)
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
    // NB: реестр с портом (host:5000/img) сломал бы split по ':' — в наших образах не встречается (P1.5-A).
    internal static string? NormalizeImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var last = image.Split('/')[^1].Split(':', 2)[0];
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }
}
