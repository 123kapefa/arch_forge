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
                            // топик — publishes (направление producer/consumer не верифицируем → ниже conf); БД — rw-db
                            var (kind, conf) = target.Kind == SystemNodeKind.Topic ? ("publishes", 0.7) : ("rw-db", 0.9);
                            edges.Add(new SystemEdge(sourceId, target.Id, kind, EdgeVia.Config, conf));
                        }
                    }

                    // http-call: URL-ключ (*Url/*Uri/*BaseAddress) со значением-URL на другой сервис —
                    // резолвим глобально после разбора всех (ключ-фильтр убирает telemetry/health-URL из шума).
                    if (IsUrlKey(key) && UrlHost(value) is { } targetHost)
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

        var httpProblems = ResolveHttpCalls(nodes, edges, httpIntents, ct);

        var graph = new SystemGraph(nodes.Values.ToList(), Dedup(edges), []);
        var problems = SystemProblemDetector.Detect(graph).Concat(httpProblems).ToList();
        return graph with { Problems = problems };
    }

    // Резолвим цель http-call по имени узла глобально (среди ВСЕХ узлов, не только Service — иначе http-URL к
    // известному брокеру/БД создал бы узел-дубль). Один узел → ребро; ноль → честный внешний узел;
    // неоднозначно (≥2) → не выдумываем цель, но и НЕ молчим: Coverage-проблема с трассой (adr/0008).
    private static List<SystemProblem> ResolveHttpCalls(
        Dictionary<string, SystemNode> nodes, List<SystemEdge> edges,
        List<(string SourceId, string TargetHost)> intents, CancellationToken ct)
    {
        var problems = new List<SystemProblem>();
        var byName = nodes.Values
            .GroupBy(n => n.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList(), StringComparer.Ordinal);

        foreach (var (sourceId, host) in intents.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var key = host.ToLowerInvariant();

            if (byName.TryGetValue(key, out var ids))
            {
                var targets = ids.Where(id => id != sourceId).ToList();
                if (targets.Count == 1)
                    edges.Add(new SystemEdge(sourceId, targets[0], "http-call", EdgeVia.Config, 0.8));
                else if (targets.Count > 1)
                    problems.Add(new SystemProblem(ProblemSeverity.Coverage, "ambiguous-target",
                        $"неоднозначная цель http-call «{host}» — {targets.Count} одноимённых узла", sourceId));
                // targets.Count == 0 (только сам себя) — self-call, пропускаем
            }
            else
            {
                var extId = "http://" + key; // регистр нормализован → нет дублей внешнего хоста
                nodes.TryAdd(extId, new SystemNode(extId, key, SystemNodeKind.External));
                edges.Add(new SystemEdge(sourceId, extId, "http-call", EdgeVia.Config, 0.4));
            }
        }
        return problems;
    }

    private static bool IsUrlKey(string key) =>
        key.Contains("url", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("uri", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("baseaddress", StringComparison.OrdinalIgnoreCase);

    // Известные публичные домены (точным суффиксом, а не голым TLD — иначе внутренние зоны .io/.dev терялись бы).
    private static readonly string[] PublicDomains =
        { ".cloudflare.com", ".githubusercontent.com", ".github.com", ".googleapis.com", ".amazonaws.com", ".windows.net" };

    // Хост сервиса из URL-значения: «http://audit-api.product-site:8080» → «audit-api».
    // null — не http-URL / userinfo / IP-литерал / localhost / инфра / bind-паттерн / известный публичный домен.
    private static string? UrlHost(string? value)
    {
        if (value is null) return null;
        var v = value.Trim();
        var sep = v.IndexOf("//", StringComparison.Ordinal);
        if (sep < 0 || !(v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return null;

        var authority = v[(sep + 2)..].Split('/', '?')[0];
        var at = authority.LastIndexOf('@');                                    // userinfo (user:pass@host) — отрезаем
        if (at >= 0) authority = authority[(at + 1)..];
        if (authority.StartsWith("[", StringComparison.Ordinal)) return null;   // IPv6-литерал — не сервис

        var fullHost = authority.Split(':')[0];
        if (fullHost.Length == 0 || fullHost.Contains("${", StringComparison.Ordinal)) return null;
        if (System.Net.IPAddress.TryParse(fullHost, out _)) return null;        // IPv4-литерал — не сервис

        var lower = fullHost.ToLowerInvariant();
        foreach (var pub in PublicDomains)
            if (lower.EndsWith(pub, StringComparison.Ordinal)) return null;

        var host = fullHost.Split('.')[0];                                      // k8s-DNS: audit-api.product-site → audit-api
        // "+"/"*" — bind-паттерны ASPNETCORE_URLS (http://+:8080); localhost/инфра — не сервис.
        if (host is "localhost" or "minio" or "+" or "*") return null;
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
