using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Реализация ISystemSource через docker-compose (P1.5-0/B/D):
// корень → папочный discovery → compose каждого сервиса → узлы/инфра → рёбра rw-db + http-call → детектор.
//
// P1.5-D — поведенческие рёбра:
//  • http-call: значения-URL в compose-env (CatalogService__Url: http://catalog-api:8080) → целевой сервис
//    резолвим глобально по имени; не разрешили → честный внешний узел.
//  • uses-bus: подключение к шине (ConnectionStrings__Bus, RabbitMQ__Host, Kafka__BootstrapServers…) →
//    НЕОРИЕНТИРОВАННОЕ ребро к Topic. Направление (publish vs subscribe) из compose НЕ достаётся —
//    честно не выдаём его (adr/0008); publishes/subscribes появятся из разбора кода (backlog).
// Известные ограничения (backlog): http-call/bus-host из appsettings.json — следующий срез.
public sealed class ComposeSystemSource(IServiceDiscoverySource? discovery = null) : ISystemSource
{
    private readonly IServiceDiscoverySource _discovery = discovery ?? new FolderDiscoverySource();

    // Уверенность http-call по достоверности разрешения цели (пропагируется в детектор циклов, adr/0008):
    private const double HttpConfident = 0.8; // цель в ТОМ ЖЕ compose — DNS-сеть compose достоверна
    private const double HttpInferred = 0.5;  // цель в ДРУГОМ compose — разрешима лишь при общей внешней сети (не верифицируем)
    private const double HttpExternal = 0.4;  // хост вне скана — честный внешний узел
    private const double BusConf = 0.7;       // uses-bus к шине того же compose: подключение есть, направление неизвестно
    private const double BusExternal = 0.4;   // uses-bus к брокеру вне скана (managed/облачный) — вне скана, ниже уверенность

    // Инфра по БАЗЕ образа (последний сегмент пути без тега) — точным совпадением, а не подстрокой.
    private static readonly Dictionary<string, SystemNodeKind> InfraByBaseImage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = SystemNodeKind.Datastore, ["postgresql"] = SystemNodeKind.Datastore,
        ["pgvector"] = SystemNodeKind.Datastore, // postgres+pgvector (образ pgvector/pgvector) — та же БД (G1)
        ["redis"] = SystemNodeKind.Datastore, ["mysql"] = SystemNodeKind.Datastore,
        ["mariadb"] = SystemNodeKind.Datastore, ["mongo"] = SystemNodeKind.Datastore,
        ["mongodb"] = SystemNodeKind.Datastore, ["minio"] = SystemNodeKind.Datastore,
        ["elasticsearch"] = SystemNodeKind.Datastore, ["opensearch"] = SystemNodeKind.Datastore,
        ["typesense"] = SystemNodeKind.Datastore, ["clickhouse"] = SystemNodeKind.Datastore,
        ["rabbitmq"] = SystemNodeKind.Topic, ["kafka"] = SystemNodeKind.Topic,
        ["redpanda"] = SystemNodeKind.Topic, ["nats"] = SystemNodeKind.Topic,
    };

    public SystemGraph Read(string root, string? env = null, CancellationToken ct = default)
    {
        var nodes = new Dictionary<string, SystemNode>(StringComparer.Ordinal);
        var edges = new List<SystemEdge>();
        var httpIntents = new List<(string SourceId, string TargetHost)>();

        // Оверлеи каждого compose + набор именованных сред (E9.1 слой 2). Материализуем список: проходим дважды
        // (сбор сред → сборка графа), а discovery источников не обязан быть многоразовым.
        var composes = ComposesToParse(root).ToList();
        var overlaysByPath = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, cp) in composes)
        {
            var ov = ComposeOverlays.Discover(cp);
            overlaysByPath[cp] = ov;
            // "override" — дефолтный оверлей (не отдельная среда); "default" зарезервирован под base-среду —
            // файл docker-compose.default.yml не должен задвоить/переопределить сентинел default.
            foreach (var k in ov.Keys)
                if (k != ComposeOverlays.Default && k != DefaultEnv) named.Add(k);
        }
        var available = new List<string> { DefaultEnv };
        available.AddRange(named);
        // выбранная среда: непустая и известная → она; иначе default (не выдумываем несуществующую среду)
        var activeEnv = !string.IsNullOrWhiteSpace(env) && available.Contains(env.Trim()) ? env.Trim() : DefaultEnv;

        foreach (var (system, composePath) in composes)
        {
            ct.ThrowIfCancellationRequested();
            ComposeFile compose;
            try { compose = LoadCompose(composePath, overlaysByPath[composePath], activeEnv); }
            catch { continue; } // битый/нечитаемый compose — пропускаем (робастность)

            var local = new Dictionary<string, SystemNode>(StringComparer.Ordinal);
            foreach (var svc in compose.Services)
            {
                var deploy = new DeployInfo(svc.Ports, svc.HasHealthcheck, svc.Networks); // E9.1: порты/hc/сети
                var node = new SystemNode($"{system}/{svc.Name}", svc.Name, Classify(svc.Image), svc.Image, deploy);
                local[svc.Name] = node;
                nodes[node.Id] = node;
            }

            foreach (var svc in compose.Services)
            {
                if (Classify(svc.Image) != SystemNodeKind.Service) continue; // рёбра рисуют приложения
                var sourceId = local[svc.Name].Id;

                foreach (var (key, value) in svc.Environment)
                {
                    // rw-db / uses-bus из строк подключения
                    // регистронезависимо: в .env/env_file ключи обычно UPPERCASE (CONNECTIONSTRINGS__DATABASE)
                    if (key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) &&
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
                            // топик — uses-bus (направление pub/sub из compose не достаётся); БД — rw-db
                            var (kind, conf) = target.Kind == SystemNodeKind.Topic ? ("uses-bus", BusConf) : ("rw-db", 0.9);
                            edges.Add(new SystemEdge(sourceId, target.Id, kind, EdgeVia.Config, conf));
                        }
                    }

                    // uses-bus из config-ключей вне connection strings (RabbitMQ__Host, Kafka__BootstrapServers…).
                    // Локальный Topic → ребро к нему; брокер вне compose (managed/облачный) → честный внешний узел
                    // (как http-call для незнакомых хостов). Направление pub/sub неизвестно. Дубли схлопнет Dedup().
                    var handledAsBus = false;
                    if (BusEndpoint.IsHostKey(key) && BusEndpoint.Host(value) is { } busHost)
                    {
                        handledAsBus = true; // строка учтена как подключение к шине → не дублируем её как http-call
                        if (local.TryGetValue(busHost, out var bus) && bus.Kind == SystemNodeKind.Topic)
                            edges.Add(new SystemEdge(sourceId, bus.Id, "uses-bus", EdgeVia.Config, BusConf));
                        else if (!local.ContainsKey(busHost))
                        {
                            var extId = "bus://" + busHost;
                            nodes.TryAdd(extId, new SystemNode(extId, busHost, SystemNodeKind.External));
                            edges.Add(new SystemEdge(sourceId, extId, "uses-bus", EdgeVia.Config, BusExternal));
                        }
                    }

                    // http-call: URL-ключ (*Url/*Uri/*BaseAddress) со значением-URL на другой сервис —
                    // резолвим глобально после разбора всех (ключ-фильтр убирает telemetry/health-URL из шума).
                    // Пропускаем, если ту же строку уже учли как подключение к шине (RabbitMQ__Uri: http://… → не двоим).
                    if (!handledAsBus && HttpUrls.IsUrlKey(key) && HttpUrls.Host(value) is { } targetHost)
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

        // http-call из appsettings*.json (reverse-proxy/gateway «Services»-мапа и др.) — Roslyn их не видит.
        httpIntents.AddRange(ConfigHttpScanner.Scan(root, nodes.Values.ToList(), ct));

        var httpProblems = ResolveHttpCalls(nodes, edges, httpIntents, ct);

        // Среды показываем, только если есть выбор (>1: default + именованные) — иначе поле опускаем (как Deploy/Endpoints).
        IReadOnlyList<SystemEnvironment>? environments = available.Count > 1
            ? available.Select(n => new SystemEnvironment(n, n == activeEnv)).ToList()
            : null;

        var graph = new SystemGraph(nodes.Values.ToList(), Dedup(edges), [], environments);
        var problems = SystemProblemDetector.Detect(graph).Concat(httpProblems).ToList();
        return graph with { Problems = problems };
    }

    // Имя среды по умолчанию (base + docker-compose.override.yml). Именованные среды — из <env>-оверлеев.
    private const string DefaultEnv = "default";

    // Резолвим цель http-call по имени узла (среди ВСЕХ узлов, не только Service — иначе http-URL к
    // известному брокеру/БД создал бы узел-дубль). DNS docker-compose локален для сети ОДНОГО compose:
    // цель в своём compose разрешаем достоверно (0.8); в чужом — лишь при общей внешней сети, которую
    // статикой не подтвердить → 0.5 (не эскалируется в цикл-Critical по совпадению имён между системами).
    // Ноль → честный внешний узел; неоднозначно → не выдумываем цель, но и НЕ молчим: Coverage (adr/0008).
    private static List<SystemProblem> ResolveHttpCalls(
        Dictionary<string, SystemNode> nodes, List<SystemEdge> edges,
        List<(string SourceId, string TargetHost)> intents, CancellationToken ct)
    {
        var problems = new List<SystemProblem>();
        var resolver = new HttpTargetResolver(nodes.Values); // общий резолвер host→узел (E3.1 срез 0)

        foreach (var (sourceId, host) in intents.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var t = resolver.Resolve(sourceId, host);
            switch (t.Kind)
            {
                case HttpTargetKind.SameSystem:                  // достоверно (DNS того же compose)
                    edges.Add(new SystemEdge(sourceId, t.NodeId!, "http-call", EdgeVia.Config, HttpConfident));
                    break;
                case HttpTargetKind.Inferred:                    // единственная цель в чужом compose — inferred
                    edges.Add(new SystemEdge(sourceId, t.NodeId!, "http-call", EdgeVia.Config, HttpInferred));
                    break;
                case HttpTargetKind.Ambiguous:                   // ≥2 одноимённых — не выдумываем
                    problems.Add(new SystemProblem(ProblemSeverity.Coverage, "ambiguous-target",
                        $"неоднозначная цель http-call «{host}» — {t.Candidates} одноимённых узла", sourceId));
                    break;
                case HttpTargetKind.External:                    // хост вне скана — честный внешний узел
                    var extId = "http://" + host.ToLowerInvariant(); // регистр нормализован → нет дублей
                    nodes.TryAdd(extId, new SystemNode(extId, host.ToLowerInvariant(), SystemNodeKind.External));
                    edges.Add(new SystemEdge(sourceId, extId, "http-call", EdgeVia.Config, HttpExternal));
                    break;
                case HttpTargetKind.SelfOnly:                    // только сам себя — пропускаем
                    break;
            }
        }
        return problems;
    }

    // Загрузка compose с наложением оверлея выбранной среды (E9.1 слой 2). Merge на «сыром» словаре →
    // влияет и на deploy, и на рёбра (env). Выбор оверлея:
    //  • default → docker-compose.override.yml (как сам docker compose применяет его поверх base);
    //  • именованная среда (dev/stage/prod) → docker-compose.<env>.yml (БЕЗ override.yml — как `docker compose -f base -f <env>`).
    private static ComposeFile LoadCompose(string composePath, IReadOnlyDictionary<string, string> overlays, string activeEnv)
    {
        var root = ComposeParser.Deserialize(File.ReadAllText(composePath));
        if (root is null) return new ComposeFile(null, []);

        var overlayKey = activeEnv == DefaultEnv ? ComposeOverlays.Default : activeEnv;
        // Наложение ИЗОЛИРОВАНО: битый/нечитаемый/несовместимый оверлей (синтаксис, спец-теги вроде !reset)
        // не должен рушить разбор валидного base — деградируем до «оверлей проигнорирован».
        if (overlays.TryGetValue(overlayKey, out var overlayPath))
            try
            {
                if (ComposeParser.Deserialize(File.ReadAllText(overlayPath)) is { } overlayRoot)
                    root = ComposeMerge.Merge(root, overlayRoot);
            }
            catch { /* оверлей невалиден → оставляем base как есть (робастность, как для самого base) */ }

        // Каталог compose нужен для G4: .env и env_file резолвятся относительно него.
        return ComposeParser.Parse(root, Path.GetDirectoryName(composePath));
    }

    private IEnumerable<(string System, string ComposePath)> ComposesToParse(string root)
    {
        if (File.Exists(root)) { yield return (DirNameOf(root), root); yield break; }
        if (!Directory.Exists(root)) yield break;

        // Пер-сервисные compose (multi-repo, у каждого сервиса свой файл).
        var discovered = _discovery.Discover(root);
        var anyPerService = false;
        foreach (var svc in discovered)
            if (svc.ComposePath is { } cp) { anyPerService = true; yield return (Rel(root, svc.Root), cp); }
        if (anyPerService) yield break;

        // Иначе — монорепо с ОДНИМ корневым compose (сервисы найдены по .sln, но своего compose у них нет):
        // откатываемся на корневые compose-файлы, иначе анализ по папке дал бы 0 узлов (G2).
        foreach (var f in FallbackComposeFiles(root))
            yield return (DirNameOf(f), f);
    }

    private static string Rel(string root, string path) => SystemNamespace.Of(root, path);

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
