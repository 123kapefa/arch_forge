using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Реализация ISystemSource через docker-compose (спайк P1.5-0):
// discovery базовых compose-файлов → сервисы/инфра → рёбра rw-db (строки подключения + depends_on) → детектор.
public sealed class ComposeSystemSource : ISystemSource
{
    // Инфра по маркеру в образе.
    private static readonly (string Marker, SystemNodeKind Kind)[] InfraImages =
    {
        ("postgres", SystemNodeKind.Datastore),
        ("mysql", SystemNodeKind.Datastore),
        ("mariadb", SystemNodeKind.Datastore),
        ("mongo", SystemNodeKind.Datastore),
        ("redis", SystemNodeKind.Datastore),
        ("minio", SystemNodeKind.Datastore),
        ("rabbitmq", SystemNodeKind.Topic),
        ("kafka", SystemNodeKind.Topic),
        ("nats", SystemNodeKind.Topic),
    };

    public SystemGraph Read(string root, CancellationToken ct = default)
    {
        var nodes = new Dictionary<string, SystemNode>(StringComparer.Ordinal);
        var edges = new List<SystemEdge>();

        foreach (var composePath in DiscoverComposeFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            ComposeFile compose;
            try { compose = ComposeParser.Parse(File.ReadAllText(composePath)); }
            catch { continue; } // битый/нечитаемый compose — пропускаем (робастность)

            foreach (var svc in compose.Services)
                nodes[svc.Name] = new SystemNode(svc.Name, svc.Name, Classify(svc.Image), svc.Image);

            foreach (var svc in compose.Services)
            {
                if (Classify(svc.Image) != SystemNodeKind.Service) continue; // рёбра рисуют приложения

                // а) строки подключения → host
                foreach (var (key, value) in svc.Environment)
                {
                    if (!key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)) continue;
                    if (ConnectionString.ExtractHost(value) is not { } host) continue;

                    if (Interpolation.IsDeferred(host))
                    {
                        // отложенный host (${KEY}) — честный внешний узел, а не пустота (adr/0008)
                        nodes.TryAdd(host, new SystemNode(host, host, SystemNodeKind.External));
                        edges.Add(new SystemEdge(svc.Name, host, "rw-db", EdgeVia.Config, 0.5, Deferred: true));
                    }
                    else if (nodes.TryGetValue(host, out var target) &&
                             target.Kind is SystemNodeKind.Datastore or SystemNodeKind.Topic)
                    {
                        edges.Add(new SystemEdge(svc.Name, host, "rw-db", EdgeVia.Config, 0.9));
                    }
                }

                // б) пол: depends_on на datastore → rw-db (если ещё нет из строки подключения)
                foreach (var dep in svc.DependsOn)
                {
                    if (!nodes.TryGetValue(dep, out var target) || target.Kind != SystemNodeKind.Datastore) continue;
                    if (edges.Any(e => e.Source == svc.Name && e.Target == dep && e.Kind == "rw-db")) continue;
                    edges.Add(new SystemEdge(svc.Name, dep, "rw-db", EdgeVia.Compose, 0.7));
                }
            }
        }

        var graph = new SystemGraph(nodes.Values.ToList(), Dedup(edges), []);
        return graph with { Problems = SharedDbDetector.Detect(graph) };
    }

    private static SystemNodeKind Classify(string? image)
    {
        if (string.IsNullOrEmpty(image)) return SystemNodeKind.Service;
        foreach (var (marker, kind) in InfraImages)
            if (image.Contains(marker, StringComparison.OrdinalIgnoreCase)) return kind;
        return SystemNodeKind.Service;
    }

    // Базовые compose-файлы (без override .dev/.prod/.build/.shared), с фильтром шума.
    private static IEnumerable<string> DiscoverComposeFiles(string root)
    {
        if (File.Exists(root)) { yield return root; yield break; } // передали сам файл
        if (!Directory.Exists(root)) yield break;

        foreach (var f in Directory.EnumerateFiles(root, "*compose*", SearchOption.AllDirectories))
        {
            var n = f.Replace('\\', '/');
            if (n.Contains("/.claude/") || n.Contains("worktrees") || n.Contains("backup") ||
                n.Contains("dbdumps") || n.Contains("/node_modules/") || n.Contains("/bin/") || n.Contains("/obj/"))
                continue;
            var name = Path.GetFileName(n).ToLowerInvariant();
            if (name is "docker-compose.yml" or "docker-compose.yaml" or "compose.yml" or "compose.yaml")
                yield return f;
        }
    }

    // Дедуп «конфиг > объявление»: одна пара (source,target,kind) → ребро с максимальной уверенностью.
    private static IReadOnlyList<SystemEdge> Dedup(List<SystemEdge> edges) =>
        edges.GroupBy(e => (e.Source, e.Target, e.Kind))
             .Select(g => g.OrderByDescending(e => e.Confidence).First())
             .ToList();
}
