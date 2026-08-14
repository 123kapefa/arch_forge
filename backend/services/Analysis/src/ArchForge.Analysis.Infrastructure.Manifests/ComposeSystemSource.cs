using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Реализация ISystemSource через docker-compose (спайк P1.5-0):
// discovery базовых compose → сервисы/инфра → рёбра rw-db (строки подключения + depends_on) → детектор.
//
// Известные ограничения спайка (закрываются в P1.5-B/P1.5-D):
//  • discovery по ИМЕНИ файла: подхватывает и инфра/мониторинг-compose (prometheus/nginx) как «сервисы»,
//    и пропускает нестандартные имена (docker-compose-dev.yaml) → нужен папочный discovery по .slnx (P1.5-B);
//  • ребро к брокеру (Topic) сейчас тоже помечается "rw-db" — честное publishes/subscribes будет в P1.5-D.
public sealed class ComposeSystemSource : ISystemSource
{
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

        foreach (var composePath in DiscoverComposeFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            ComposeFile compose;
            try { compose = ComposeParser.Parse(File.ReadAllText(composePath)); }
            catch { continue; } // битый/нечитаемый compose — пропускаем (робастность)

            // Пространство имён узла = имя compose-проекта (или каталог файла): одинаковые имена сервисов
            // в РАЗНЫХ compose (api/db) не должны сливаться в один узел — иначе тихо теряем систему (честность).
            var system = SystemName(compose, composePath);

            // Локальные узлы файла: svcName → node с namespaced Id. Рёбра резолвим ВНУТРИ файла.
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

                // а) строки подключения → host
                foreach (var (key, value) in svc.Environment)
                {
                    if (!key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)) continue;
                    if (ConnectionString.ExtractHost(value) is not { } host) continue;

                    if (Interpolation.IsDeferred(host))
                    {
                        // отложенный host (${KEY}) — честный внешний узел, а не пустота (adr/0008)
                        nodes.TryAdd(host, new SystemNode(host, host, SystemNodeKind.External));
                        edges.Add(new SystemEdge(sourceId, host, "rw-db", EdgeVia.Config, 0.5, Deferred: true));
                    }
                    else if (local.TryGetValue(host, out var target) &&
                             target.Kind is SystemNodeKind.Datastore or SystemNodeKind.Topic)
                    {
                        edges.Add(new SystemEdge(sourceId, target.Id, "rw-db", EdgeVia.Config, 0.9));
                    }
                }

                // б) пол: depends_on на datastore → rw-db (если ещё нет из строки подключения)
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

    private static string SystemName(ComposeFile compose, string composePath) =>
        !string.IsNullOrWhiteSpace(compose.Name)
            ? compose.Name!
            : Path.GetFileName(Path.GetDirectoryName(composePath)) is { Length: > 0 } dir ? dir : "system";

    // База образа: последний сегмент пути без тега. «quay.io/community/postgres-exporter:v1» → «postgres-exporter».
    private static SystemNodeKind Classify(string? image)
    {
        if (string.IsNullOrEmpty(image)) return SystemNodeKind.Service;
        var baseName = image.Split('/')[^1].Split(':')[0];
        return InfraByBaseImage.TryGetValue(baseName, out var kind) ? kind : SystemNodeKind.Service;
    }

    // Базовые compose-файлы (без override .dev/.prod/.build/.shared), с фильтром шума. Недоступные
    // каталоги игнорируем (IgnoreInaccessible) — не роняем весь разбор из-за одной папки без прав.
    private static IReadOnlyList<string> DiscoverComposeFiles(string root)
    {
        if (File.Exists(root)) return [root];              // передали сам файл
        if (!Directory.Exists(root)) return [];

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var found = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*compose*", opts))
        {
            var n = f.Replace('\\', '/');
            if (ContainsAny(n, "/.claude/", "worktrees", "backup", "dbdumps", "/node_modules/", "/bin/", "/obj/"))
                continue;
            var name = Path.GetFileName(n).ToLowerInvariant();
            if (name is "docker-compose.yml" or "docker-compose.yaml" or "compose.yml" or "compose.yaml")
                found.Add(f);
        }
        found.Sort(StringComparer.Ordinal); // детерминированный порядок между запусками/машинами
        return found;
    }

    private static bool ContainsAny(string s, params string[] needles)
    {
        foreach (var needle in needles)
            if (s.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Дедуп «конфиг > объявление»: одна пара (source,target,kind) → ребро с максимальной уверенностью.
    private static IReadOnlyList<SystemEdge> Dedup(List<SystemEdge> edges) =>
        edges.GroupBy(e => (e.Source, e.Target, e.Kind))
             .Select(g => g.OrderByDescending(e => e.Confidence).First())
             .ToList();
}
