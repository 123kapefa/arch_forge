using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

/// <summary>Результат per-service код-разбора; <c>Result == null</c> — сервис не разобрался (изоляция ошибок).</summary>
public sealed record ServiceAnalysis(DiscoveredService Service, string Location, AnalysisResult? Result)
{
    // minimal-API эндпоинты сервиса (app.MapGet/…); контроллеры идут через Result.Types[].Members. #71
    public IReadOnlyList<EndpointHit> MinimalEndpoints { get; init; } = [];
}

/// <summary>Хелперы над результатами разбора — единая точка понятия «сервис не разобрался».</summary>
public static class ServiceAnalysisExtensions
{
    /// <summary>Сервисы, чей код не разобрался (изоляция ошибок) — для деградации/пропуска.</summary>
    public static IEnumerable<ServiceAnalysis> Failed(this IReadOnlyList<ServiceAnalysis> analyses) =>
        analyses.Where(a => a.Result is null);
}

// Общий per-service код-разбор для линз, требующих КОДА (дрейф от свода, нарушения слоёв):
// discovery (только сервисы С compose — у них есть узлы в графе, к которым привяжется location) +
// Фаза-1 анализ каждого сервиса. Единый источник, чтобы линзы не дублировали обход/фильтр/изоляцию.
public sealed class ServiceAnalyzer(IServiceDiscoverySource discovery, ITypeSource types, TypeClassifier classifier,
    IEndpointSource? endpoints = null)
{
    // Кап параллелизма per-service разбора: Roslyn на сервис тяжёл (память компиляции + CPU). Берём долю ядер
    // (≥1) — чтобы не пробуксовывать на памяти и оставить машине воздух. Детерминизм не страдает: результаты
    // кладём по индексу, порядок = discovery. Вынесено в тестируемую единицу.
    private const int CoreFraction = 2; // половина ядер (каждая компиляция грузит runtime-ссылки → память)
    internal static int MaxParallelism => Math.Max(1, Environment.ProcessorCount / CoreFraction);

    public IReadOnlyList<ServiceAnalysis> Analyze(string root, ConventionPack pack, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return []; // код-разбор идёт по коду сервисов — нужен корень-папка

        // только сервисы С compose (у них есть узлы в графе, к которым привяжется location)
        var services = discovery.Discover(root).Where(s => s.ComposePath is not null).ToList();
        if (services.Count == 0) return [];

        // Ограниченно-параллельный разбор: на 15–20 сервисах последовательный Roslyn = десятки секунд.
        // Каждый сервис — своя изолированная компиляция без общего мутабельного состояния → потокобезопасно
        // (TypeClassifier без состояния). Результаты — по индексу, поэтому порядок остаётся детерминированным.
        var results = new ServiceAnalysis[services.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct };
        Parallel.For(0, services.Count, options, i =>
        {
            var svc = services[i];
            var location = SystemNamespace.Of(root, svc.Root); // namespace-префикс узлов сервиса ({system})
            AnalysisResult? result;
            try { result = classifier.Classify(types.Read(svc.Root, ct), pack); }
            catch (OperationCanceledException) { throw; }  // отмена — наверх (Parallel.For сведёт к OCE)
            catch { result = null; }                       // сбой сервиса → null: не роняем скан, линза поднимет Coverage (adr/0008)
            IReadOnlyList<EndpointHit> mini = [];          // minimal-API эндпоинты (#71) — отдельный try: их сбой не роняет разбор
            try { mini = endpoints?.Read(svc.Root, ct) ?? []; }
            catch (OperationCanceledException) { throw; }
            catch { mini = []; }
            results[i] = new ServiceAnalysis(svc, location, result) { MinimalEndpoints = mini };
        });
        return results;
    }
}
