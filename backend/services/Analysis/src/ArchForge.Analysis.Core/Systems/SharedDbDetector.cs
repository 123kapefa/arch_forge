namespace ArchForge.Analysis.Core.Systems;

// Детектор системного анти-паттерна «общая БД»: в одно хранилище пишут ≥2 РАЗНЫХ приложения.
// Различаем пишущих по НОРМАЛИЗОВАННОМУ образу (без реестра/тега), а не по контейнеру —
// иначе api + migrate одного сервиса (один образ, разные контейнеры) дали бы ложную «общую БД».
// Чистая функция поверх собранного графа (как линт Фазы 1). Честность: отложенные (Deferred) рёбра
// не считаем — target ещё не разрешён, выдумывать нарушение нельзя (adr/0008).
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
                .Select(e => WriterIdentity(e.Source, nodeById))
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

    // Идентичность пишущего = нормализованный образ (api и migrate одного образа = одно приложение).
    private static string WriterIdentity(string sourceId, IReadOnlyDictionary<string, SystemNode> nodeById) =>
        nodeById.TryGetValue(sourceId, out var n) && NormalizeImage(n.Image) is { } img ? img : sourceId;

    // «ghcr.io/ns/kalmykov-auth-api:stage» → «kalmykov-auth-api» (без реестра и тега).
    internal static string? NormalizeImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var noTag = image.Split(':', 2)[0];      // до первого ':' (порт реестра в наших образах не встречается)
        var last = noTag.Split('/')[^1];
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }
}
