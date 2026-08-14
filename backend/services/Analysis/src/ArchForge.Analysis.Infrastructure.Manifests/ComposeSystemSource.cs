using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Реализация ISystemSource через docker-compose (P1.5-0/B/D):
// корень → папочный discovery → compose каждого сервиса → узлы/инфра → рёбра rw-db + http-call → детектор.
//
// P1.5-D — поведенческие рёбра:
//  • http-call: значения-URL в compose-env (CatalogService__Url: http://catalog-api:8080) → целевой сервис
//    резолвим глобально по имени; не разрешили → честный внешний узел.
//  • publishes/subscribes: на реальном таргете шины нет (Loyalty.Communication = HTTP-клиент) — честно отсутствуют.
// Известные ограничения (backlog): http-call из appsettings.json (напр. auth→sms) — следующий срез;
//  publish/subscribe появится при наличии шины (MassTransit/rabbit/kafka).
public sealed class ComposeSystemSource(IServiceDiscoverySource? discovery = null) : ISystemSource
{
    private readonly IServiceDiscoverySource _discovery = discovery ?? new FolderDiscoverySource();

    // Инфра по БАЗЕ образа (последний сегмент пути без тега) — точным совпадением, а не подстрокой.
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
        var httpIntents = new List<(string SourceId, string TargetHost)>();

        foreach (var (system, composePath) in ComposesToParse(root))
        {
            ct.ThrowIfCancellationRequested();
            ComposeFile compose;
            try { compose = ComposeParser.Parse(File.ReadAllText(composePath)); }
            catch { continue; } // битый/нечитаемый compose — пропускаем (робастность)

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
                    // rw-db / publishes из строк подключения
                    if (key.StartsWith("ConnectionStrings__", StringComparison.Ordinal) &&
                        ConnectionString.ExtractHost(value) is { } host)
                    {
                        if (Interpolation.IsDeferred(host))
                        {
                            nodes.TryAdd(host, new SystemNode(host, host, SystemNodeKind.External));
                            edges.Add(new SystemEdge(sourceId, host, "rw-db", EdgeVia.Config, 0.5, Deferred: true));
                        }
                        else if (local.TryGetValue(host, out var target) &&
                                 target.Kind is SystemNodeKind.Datastore or SystemNodeKind.Topic)
                        {
                            var kind = target.Kind == SystemNodeKind.Topic ? "publishes" : "rw-db";
                            edges.Add(new SystemEdge(sourceId, target.Id, kind, EdgeVia.Config, 0.9));
                        }
                    }

                    // http-call: значение — URL на другой сервис (резолвим глобально после разбора всех)
                    if (UrlHost(value) is { } targetHost)
                        httpIntents.Add((sourceId, targetHost));
                }

                foreach (var dep in svc.DependsOn)
                {
                    if (!local.TryGetValue(dep, out var target) || target.Kind != SystemNodeKind.Datastore) continue;
                    if (edges.Any(e => e.Source == sourceId && e.Target == target.Id && e.Kind == "rw-db")) continue;
                    edges.Add(new SystemEdge(sourceId, target.Id, "rw-db", EdgeVia.Compose, 0.7));
                }
            }
        }

        ResolveHttpCalls(nodes, edges, httpIntents);

        var graph = new SystemGraph(nodes.Values.ToList(), Dedup(edges), []);
        return graph with { Problems = SharedDbDetector.Detect(graph) };
    }

    // Резолвим цель http-call по имени сервиса глобально: попал в один сервис → ребро; иначе внешний узел (честно).
    private static void ResolveHttpCalls(
        Dictionary<string, SystemNode> nodes, List<SystemEdge> edges,
        List<(string SourceId, string TargetHost)> intents)
    {
        var appByName = nodes.Values
            .Where(n => n.Kind == SystemNodeKind.Service)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (sourceId, host) in intents.Distinct())
        {
            if (appByName.TryGetValue(host, out var ids) && ids.Count == 1 && ids[0] != sourceId)
            {
                edges.Add(new SystemEdge(sourceId, ids[0], "http-call", EdgeVia.Config, 0.8));
            }
            else if (!appByName.ContainsKey(host)) // неизвестный/внешний сервис — честный внешний узел
            {
                var extId = "http://" + host;
                nodes.TryAdd(extId, new SystemNode(extId, host, SystemNodeKind.External));
                edges.Add(new SystemEdge(sourceId, extId, "http-call", EdgeVia.Config, 0.4));
            }
            // неоднозначное имя (ids.Count > 1) — не выдумываем цель (adr/0008)
        }
    }

    // Хост сервиса из URL-значения: «http://audit-api.product-site:8080» → «audit-api».
    // null — если не http-URL, localhost/инфра или публичный интернет-адрес (не наш сервис).
    private static string? UrlHost(string? value)
    {
        if (value is null) return null;
        var v = value.Trim();
        var sep = v.IndexOf("//", StringComparison.Ordinal);
        if (sep < 0 || !(v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return null;

        var authority = v[(sep + 2)..].Split('/', '?')[0]; // host[:port]
        var fullHost = authority.Split(':')[0];
        if (fullHost.Contains("${", StringComparison.Ordinal)) return null; // отложенное — не резолвим

        var segs = fullHost.Split('.');
        if (segs.Length > 1 &&
            segs[^1].ToLowerInvariant() is "com" or "org" or "io" or "net" or "ru" or "dev" or "cloud" or "app")
            return null; // публичный интернет-адрес (cloudflare/github) — не наш сервис

        var host = segs[0];
        // "+"/"*" — bind-паттерны ASPNETCORE_URLS (http://+:8080), не адрес вызова; localhost/инфра — тоже не сервис.
        if (host.Length == 0 || host is "localhost" or "minio" or "+" or "*" ||
            host.StartsWith("127.", StringComparison.Ordinal) || host.StartsWith("0.0", StringComparison.Ordinal))
            return null;
        return host;
    }

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
        var baseName = image.Split('/')[^1].Split(':')[0];
        return InfraByBaseImage.TryGetValue(baseName, out var kind) ? kind : SystemNodeKind.Service;
    }

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
