using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Реализация ISystemSource через docker-compose (P1.5-0/B):
// корень → папочный discovery сервисов (по .slnx) → compose каждого сервиса → узлы/инфра → рёбра rw-db → детектор.
//
// Известные ограничения (закрываются в P1.5-D):
//  • ребро к брокеру (Topic) сейчас тоже помечается "rw-db" — честное publishes/subscribes будет в P1.5-D.
public sealed class ComposeSystemSource(IServiceDiscoverySource? discovery = null) : ISystemSource
{
    private readonly IServiceDiscoverySource _discovery = discovery ?? new FolderDiscoverySource();

    // Инфра по БАЗЕ образа (последний сегмент пути без тега) — точным совпадением, а не подстрокой,
    // иначе «postgres-exporter»/«redis_exporter» ошибочно попали бы в Datastore.
    private static readonly Dictionary<string, SystemNodeKind> InfraByBaseImage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = SystemNodeKind.Datastore, ["postgresql"] = SystemNodeKind.Datastore,
        ["redis"] = SystemNodeKind.Datastore, ["mysql"] = SystemNodeKind.Datastore,
        ["mariadb"] = SystemNodeKind.Datastore, ["mongo"] = SystemNodeKind.Datastore,
        ["mongodb"] = SystemNodeKind.Datastore, ["minio"] = SystemNodeKind.Datastore,
        ["rabbitmq"] = SystemNodeKind.Topic, ["kafka"] = SystemNodeKind.Topic,
        ["redpanda"] = SystemNodeKind.Topic, ["nats"] = SystemNodeKind.Topic,
    };

    public SystemGraph Read(string root, CancellationToken ct = default)
    {
        var nodes = new Dictionary<string, SystemNode>(StringComparer.Ordinal);
        var edges = new List<SystemEdge>();

        foreach (var (system, composePath) in ComposesToParse(root))
        {
            ct.ThrowIfCancellationRequested();
            ComposeFile compose;
            try { compose = ComposeParser.Parse(File.ReadAllText(composePath)); }
            catch { continue; } // битый/нечитаемый compose — пропускаем (робастность)

            // Пространство имён узла = система (сервис из discovery / каталог): одинаковые имена сервисов
            // в РАЗНЫХ compose (api/db) не сливаются в один узел — иначе тихо теряем систему (честность).
            var local = new Dictionary<string, SystemNode>(StringComparer.Ordinal);
            foreach (var svc in compose.Services)
            {
                var node = new SystemNode($"{system}/{svc.Name}", svc.Name, Classify(svc.Image), svc.Image);
                local[svc.Name] = node;
                nodes[node.Id] = node;
            }

            foreach (var svc in compose.Services)
            {
                if (Classify(svc.Image) != SystemNodeKind.Service) continue; // рёбра рисуют приложения
                var sourceId = local[svc.Name].Id;

                foreach (var (key, value) in svc.Environment)
                {
                    if (!key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)) continue;
                    if (ConnectionString.ExtractHost(value) is not { } host) continue;

                    if (Interpolation.IsDeferred(host))
                    {
                        nodes.TryAdd(host, new SystemNode(host, host, SystemNodeKind.External));
                        edges.Add(new SystemEdge(sourceId, host, "rw-db", EdgeVia.Config, 0.5, Deferred: true));
                    }
                    else if (local.TryGetValue(host, out var target) &&
                             target.Kind is SystemNodeKind.Datastore or SystemNodeKind.Topic)
                    {
                        edges.Add(new SystemEdge(sourceId, target.Id, "rw-db", EdgeVia.Config, 0.9));
                    }
                }

                foreach (var dep in svc.DependsOn)
                {
                    if (!local.TryGetValue(dep, out var target) || target.Kind != SystemNodeKind.Datastore) continue;
                    if (edges.Any(e => e.Source == sourceId && e.Target == target.Id && e.Kind == "rw-db")) continue;
                    edges.Add(new SystemEdge(sourceId, target.Id, "rw-db", EdgeVia.Compose, 0.7));
                }
            }
        }

        var graph = new SystemGraph(nodes.Values.ToList(), Dedup(edges), []);
        return graph with { Problems = SharedDbDetector.Detect(graph) };
    }

    // Что и под каким пространством имён парсить:
    //  • root — файл           → сам файл (namespace = его каталог);
    //  • root — папка + сервисы → compose каждого сервиса (namespace = ПАПКА сервиса относительно корня —
    //    уникальна, в отличие от имени solution: два сервиса с одинаковым Server.sln не сольются, ревью #1);
    //  • root — папка без .slnx → fallback: базовые compose под корнем (namespace = их каталог).
    private IEnumerable<(string System, string ComposePath)> ComposesToParse(string root)
    {
        if (File.Exists(root)) { yield return (DirNameOf(root), root); yield break; }
        if (!Directory.Exists(root)) yield break;

        var discovered = _discovery.Discover(root);
        if (discovered.Count > 0)
        {
            foreach (var svc in discovered)
                if (svc.ComposePath is { } cp) yield return (Rel(root, svc.Root), cp);
            yield break;
        }

        foreach (var f in FallbackComposeFiles(root))
            yield return (DirNameOf(f), f);
    }

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string DirNameOf(string filePath) =>
        Path.GetFileName(Path.GetDirectoryName(filePath)) is { Length: > 0 } d ? d : "system";

    private static SystemNodeKind Classify(string? image)
    {
        if (string.IsNullOrEmpty(image)) return SystemNodeKind.Service;
        var baseName = image.Split('/')[^1].Split(':')[0]; // «quay.io/community/postgres-exporter:v1» → «postgres-exporter»
        return InfraByBaseImage.TryGetValue(baseName, out var kind) ? kind : SystemNodeKind.Service;
    }

    // Fallback: базовые compose под корнем (для каталога без .slnx). Недоступные папки игнорируем.
    private static IReadOnlyList<string> FallbackComposeFiles(string root)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var found = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*compose*", opts))
        {
            if (PathFilter.IsNoise(root, f)) continue;
            var name = Path.GetFileName(f).ToLowerInvariant();
            if (name is "docker-compose.yml" or "docker-compose.yaml" or "compose.yml" or "compose.yaml")
                found.Add(f);
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static IReadOnlyList<SystemEdge> Dedup(List<SystemEdge> edges) =>
        edges.GroupBy(e => (e.Source, e.Target, e.Kind))
             .Select(g => g.OrderByDescending(e => e.Confidence).First())
             .ToList();
}
