using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

/// <summary>Результат per-service код-разбора; <c>Result == null</c> — сервис не разобрался (изоляция ошибок).</summary>
public sealed record ServiceAnalysis(DiscoveredService Service, string Location, AnalysisResult? Result)
{
    // minimal-API эндпоинты сервиса (app.MapGet/…); контроллеры идут через Result.Types[].Members. #71
    public IReadOnlyList<EndpointHit> MinimalEndpoints { get; init; } = [];

    // исходящие HTTP-вызовы сервиса (HttpClient.GetAsync/… с абсолютным URL) — питает call-graph (E3.1).
    public IReadOnlyList<HttpCallHit> HttpCalls { get; init; } = [];

    // Монорепо с общим compose: узел графа, к которому сервис привязан по имени (ServiceNodeMatcher). Есть → проблемы, вызовы и слои
    // идут в ЭТОТ узел, а не по namespace системы (в ней десятки узлов). null — у сервиса свой compose, привязка по namespace, как раньше.
    public string? NodeId { get; init; }
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
    IEndpointSource? endpoints = null, IHttpCallSource? httpCalls = null)
{
    // Кап параллелизма per-service разбора: Roslyn на сервис тяжёл (память компиляции + CPU). Берём долю ядер
    // (≥1) — чтобы не пробуксовывать на памяти и оставить машине воздух. Детерминизм не страдает: результаты
    // кладём по индексу, порядок = discovery. Вынесено в тестируемую единицу.
    private const int CoreFraction = 2; // половина ядер (каждая компиляция грузит runtime-ссылки → память)
    internal static int MaxParallelism => Math.Max(1, Environment.ProcessorCount / CoreFraction);

    // nodes/naming — узлы графа и правила имён: нужны только для монорепо с общим compose (см. ServiceNodeMatcher).
    public IReadOnlyList<ServiceAnalysis> Analyze(string root, ConventionPack pack, CancellationToken ct = default,
        IReadOnlyList<SystemNode>? nodes = null, Profiles.ServiceNaming? naming = null)
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
            results[i] = new ServiceAnalysis(svc, location, result) { MinimalEndpoints = mini, HttpCalls = calls, NodeId = boundNode?.Id };
        });
        return results;
    }
}
