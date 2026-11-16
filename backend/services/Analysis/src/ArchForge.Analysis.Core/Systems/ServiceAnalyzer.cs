using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

/// <summary>Результат per-service код-разбора; <c>Result == null</c> — сервис не разобрался (изоляция ошибок).</summary>
public sealed record ServiceAnalysis(DiscoveredService Service, string Location, AnalysisResult? Result)
{
    // minimal-API эндпоинты сервиса (app.MapGet/…); контроллеры идут через Result.Types[].Members. #71
    public IReadOnlyList<EndpointHit> MinimalEndpoints { get; init; } = [];

    // исходящие HTTP-вызовы сервиса (HttpClient.GetAsync/… с абсолютным URL) — питает call-graph (E3.1).
    public IReadOnlyList<HttpCallHit> HttpCalls { get; init; } = [];

    // DI-контейнер сервиса: регистрации времён жизни + зависимости конструкторов (E4.2). null — не разбирался (источник не подключён/сбой).
    public DiModel? Di { get; init; }

    // Монорепо с общим compose: узел графа, к которому сервис привязан по имени (ServiceNodeMatcher). Есть → проблемы, вызовы и слои
    // идут в ЭТОТ узел, а не по namespace системы (в ней десятки узлов). null — у сервиса свой compose, привязка по namespace, как раньше.
    public string? NodeId { get; init; }

    // проверки параметров на null (правило redundant-null-check); читаются вместе с остальным разбором сервиса
    public IReadOnlyList<NullGuardSite> Guards { get; init; } = [];

    // публикации/подписки шины сообщений из кода (направление pub/sub, #61) — питает рёбра publishes/subscribes
    public IReadOnlyList<BusSite> BusSites { get; init; } = [];

    // чем сервис сообщает об ожидаемых ошибках (правило mixed-error-strategy)
    public ErrorStrategyFacts ErrorStrategy { get; init; } = ErrorStrategyFacts.Empty;

    // правила формата в валидаторах и Value Object-ы (правило double-validation)
    public ValidationFacts Validation { get; init; } = ValidationFacts.Empty;

    // часы в домене и аудит в interceptor (правило aggregate-time)
    public AggregateTimeFacts AggregateTime { get; init; } = AggregateTimeFacts.Empty;

    // раскладка по папкам и имена файлов (правило folder-structure)
    public FolderFacts Folders { get; init; } = FolderFacts.Empty;

    // мутации агрегата без доменного события / UpdatedAt (правило aggregate-events)
    public AggregateEventFacts AggregateEvents { get; init; } = AggregateEventFacts.Empty;

    // Файлов в сервисе больше капа разбора → разобрана только часть, карта по нему неполная (#49, adr/0008).
    public int? TruncatedAtFiles { get; init; }
    public bool Truncated => TruncatedAtFiles is not null;
}

/// <summary>Хелперы над результатами разбора — единая точка понятия «сервис не разобрался».</summary>
public static class ServiceAnalysisExtensions
{
    /// <summary>Сервисы, чей код не разобрался (изоляция ошибок) — для деградации/пропуска.</summary>
    public static IEnumerable<ServiceAnalysis> Failed(this IReadOnlyList<ServiceAnalysis> analyses) =>
        analyses.Where(a => a.Result is null);

    /// <summary>Тройки для детекторов проблем: место проблемы — узел сервиса, если он привязан (монорепо), иначе namespace системы.</summary>
    public static IEnumerable<(DiscoveredService Svc, string Location, AnalysisResult? Result)> ForProblems(this IReadOnlyList<ServiceAnalysis> analyses) =>
        analyses.Select(a => (a.Service, a.NodeId ?? a.Location, a.Result));
}

// Общий per-service код-разбор для линз, требующих КОДА (дрейф от свода, нарушения слоёв):
// discovery (только сервисы С compose — у них есть узлы в графе, к которым привяжется location) +
// Фаза-1 анализ каждого сервиса. Единый источник, чтобы линзы не дублировали обход/фильтр/изоляцию.
public sealed class ServiceAnalyzer(IServiceDiscoverySource discovery, ITypeSource types, TypeClassifier classifier,
    IEndpointSource? endpoints = null, IHttpCallSource? httpCalls = null, IClientLibraryResolver? libraries = null, IGuardSource? guards = null, IErrorStrategySource? errors = null, IValidationSource? validation = null, IAggregateTimeSource? aggregateTime = null, IFolderSource? folders = null, IBusSource? bus = null, IDiSource? di = null, IAggregateEventSource? aggregateEvents = null, IScanLimitSource? scanLimits = null)
{
    // Кап параллелизма per-service разбора: Roslyn на сервис тяжёл (память компиляции + CPU). Берём долю ядер
    // (≥1) — чтобы не пробуксовывать на памяти и оставить машине воздух. Детерминизм не страдает: результаты
    // кладём по индексу, порядок = discovery. Вынесено в тестируемую единицу.
    private const int CoreFraction = 2; // половина ядер (каждая компиляция грузит runtime-ссылки → память)
    internal static int MaxParallelism => Math.Max(1, Environment.ProcessorCount / CoreFraction);

    // Решение со своим compose, в котором ≥2 сервисов, заменяем папками проектов, привязанными к узлам (ProjectFolderSplitter).
    // Не разделилось (мало совпадений) — решение остаётся целиком, как раньше.
    private static List<DiscoveredService> SplitByProjectFolders(string root, List<DiscoveredService> services,
        IReadOnlyList<DiscoveredService> discovered, IReadOnlyList<SystemNode> nodes, Profiles.ServiceNaming naming,
        Dictionary<string, SystemNode> bound, CancellationToken ct)
    {
        var roots = discovered.Select(d => d.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredService>();
        foreach (var svc in services)
        {
            ct.ThrowIfCancellationRequested();
            var parts = svc.ComposePath is null ? [] : ProjectFolderSplitter.Split(root, svc, nodes, naming, roots, ct);
            if (parts.Count == 0) { result.Add(svc); continue; }
            foreach (var (sub, node) in parts)
            {
                result.Add(sub);
                bound[sub.Root] = node;
            }
        }
        return result;
    }

    // nodes/naming — узлы графа и правила имён: нужны только для монорепо с общим compose (см. ServiceNodeMatcher).
    public IReadOnlyList<ServiceAnalysis> Analyze(string root, ConventionPack pack, CancellationToken ct = default,
        IReadOnlyList<SystemNode>? nodes = null, Profiles.ServiceNaming? naming = null, bool readGuards = true, bool readErrors = true, bool readValidation = true, bool readAggregateTime = true, bool readFolders = true, bool readBus = false, bool readAggregateEvents = true, bool readDi = false)
    {
        if (!Directory.Exists(root)) return []; // код-разбор идёт по коду сервисов — нужен корень-папка

        var discovered = discovery.Discover(root);
        // сервисы С compose (у них есть узлы в графе, к которым привяжется location)
        var services = discovered.Where(s => s.ComposePath is not null).ToList();
        // Монорепо: НИ У ОДНОГО сервиса нет своего compose (граф собран из корневого) → берём сервисы, однозначно привязанные к узлу по имени.
        var bound = new Dictionary<string, SystemNode>(StringComparer.OrdinalIgnoreCase);   // корень сервиса → его узел
        if (services.Count == 0 && nodes is { Count: > 0 })
        {
            foreach (var (svc, node) in ServiceNodeMatcher.Match(discovered, nodes, naming ?? Profiles.ServiceNaming.Default))
            {
                services.Add(svc);
                bound[svc.Root] = node;
            }
        }
        // Одно решение + общий compose с несколькими сервисами: делим решение по папкам проектов, дрейф идёт в узел сервиса (#60).
        if (nodes is { Count: > 0 } && services.Any(s => s.ComposePath is not null))
            services = SplitByProjectFolders(root, services, discovered, nodes, naming ?? Profiles.ServiceNaming.Default, bound, ct);
        if (services.Count == 0) return [];

        // Ограниченно-параллельный разбор: на 15–20 сервисах последовательный Roslyn = десятки секунд.
        // Каждый сервис — своя изолированная компиляция без общего мутабельного состояния → потокобезопасно
        // (TypeClassifier без состояния). Результаты — по индексу, поэтому порядок остаётся детерминированным.
        var results = new ServiceAnalysis[services.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct };
        Parallel.For(0, services.Count, options, i =>
        {
            var svc = services[i];
            bound.TryGetValue(svc.Root, out var boundNode);
            // монорепо: location = система узла (общий префикс id), сам узел — в NodeId; иначе namespace-префикс узлов сервиса ({system})
            var location = boundNode is not null ? HttpTargetResolver.SystemOf(boundNode.Id) : SystemNamespace.Of(root, svc.Root);
            AnalysisResult? result;
            try { result = classifier.Classify(types.Read(svc.Root, ct), pack); }
            catch (OperationCanceledException) { throw; }  // отмена — наверх (Parallel.For сведёт к OCE)
            catch { result = null; }                       // сбой сервиса → null: не роняем скан, линза поднимет Coverage (adr/0008)
            IReadOnlyList<EndpointHit> mini = [];          // minimal-API эндпоинты (#71) — отдельный try: их сбой не роняет разбор
            try { mini = endpoints?.Read(svc.Root, ct) ?? []; }
            catch (OperationCanceledException) { throw; }
            catch { mini = []; }
            IReadOnlyList<HttpCallHit> calls = [];         // исходящие http-вызовы (E3.1) — свой try: их сбой не роняет разбор
            try { calls = httpCalls?.Read(svc.Root, ct) ?? []; }
            catch (OperationCanceledException) { throw; }
            catch { calls = []; }
            // вызовы из клиентских библиотек (*.Contracts), которые сервис подключает и регистрирует, — это ЕГО вызовы (E3.1);
            // адрес по ключу конфигурации разрешится по конфигурации этого сервиса. Свой try: сбой не роняет разбор.
            try
            {
                if (libraries is not null && httpCalls is not null)
                {
                    var fromLibs = new List<HttpCallHit>(calls);
                    foreach (var lib in libraries.Resolve(svc.Root, root, ct))
                        foreach (var hit in httpCalls.Read(lib.Dir, ct))
                            fromLibs.Add(hit with { Evidence = $"{hit.Evidence} [библиотека {lib.Name}]" });
                    calls = fromLibs;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* библиотеки не разобрались — остаются собственные вызовы */ }
            IReadOnlyList<NullGuardSite> guardSites = [];  // проверки на null — свой try: их сбой не роняет разбор
            try { guardSites = readGuards ? guards?.Read(svc.Root, ct) ?? [] : []; }
            catch (OperationCanceledException) { throw; }
            catch { guardSites = []; }
            IReadOnlyList<BusSite> busSites = [];          // pub/sub шины (#61) — свой try; читаем лишь по запросу (линза вызовов)
            try { busSites = readBus ? bus?.Read(svc.Root, ct) ?? [] : []; }
            catch (OperationCanceledException) { throw; }
            catch { busSites = []; }
            var errorFacts = ErrorStrategyFacts.Empty;     // стратегия ошибок — тоже свой try
            try { errorFacts = readErrors ? errors?.Read(svc.Root, ct) ?? ErrorStrategyFacts.Empty : ErrorStrategyFacts.Empty; }
            catch (OperationCanceledException) { throw; }
            catch { errorFacts = ErrorStrategyFacts.Empty; }
            var validationFacts = ValidationFacts.Empty;   // правила валидации — тоже свой try
            try { validationFacts = readValidation ? validation?.Read(svc.Root, ct) ?? ValidationFacts.Empty : ValidationFacts.Empty; }
            catch (OperationCanceledException) { throw; }
            catch { validationFacts = ValidationFacts.Empty; }
            var timeFacts = AggregateTimeFacts.Empty;      // время и аудит — тоже свой try
            try { timeFacts = readAggregateTime ? aggregateTime?.Read(svc.Root, ct) ?? AggregateTimeFacts.Empty : AggregateTimeFacts.Empty; }
            catch (OperationCanceledException) { throw; }
            catch { timeFacts = AggregateTimeFacts.Empty; }
            var folderFacts = FolderFacts.Empty;           // раскладка — тоже свой try
            try { folderFacts = readFolders ? folders?.Read(svc.Root, ct) ?? FolderFacts.Empty : FolderFacts.Empty; }
            catch (OperationCanceledException) { throw; }
            catch { folderFacts = FolderFacts.Empty; }
            var eventFacts = AggregateEventFacts.Empty;    // события в агрегате — тоже свой try
            try { eventFacts = readAggregateEvents ? aggregateEvents?.Read(svc.Root, ct) ?? AggregateEventFacts.Empty : AggregateEventFacts.Empty; }
            catch (OperationCanceledException) { throw; }
            catch { eventFacts = AggregateEventFacts.Empty; }
            DiModel? diModel = null;                       // DI-контейнер (E4.2) — читаем лишь по запросу (линза слоёв); сбой → null, детектор молчит
            try { diModel = readDi ? di?.Read(svc.Root, ct) : null; }
            catch (OperationCanceledException) { throw; }
            catch { diModel = null; }
            int? truncatedAt = null;                         // кап числа файлов — свой try: сбой проверки не роняет разбор
            try { if (scanLimits?.IsTruncated(svc.Root, ct) == true) truncatedAt = scanLimits.MaxFiles; }
            catch (OperationCanceledException) { throw; }
            catch { truncatedAt = null; }
            results[i] = new ServiceAnalysis(svc, location, result) { TruncatedAtFiles = truncatedAt,  AggregateEvents = eventFacts, Folders = folderFacts, AggregateTime = timeFacts, Validation = validationFacts, MinimalEndpoints = mini, HttpCalls = calls, NodeId = boundNode?.Id, Guards = guardSites, ErrorStrategy = errorFacts, BusSites = busSites, Di = diModel };
        });
        return results;
    }
}
