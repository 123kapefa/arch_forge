using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

/// <summary>Результат per-service код-разбора; <c>Result == null</c> — сервис не разобрался (изоляция ошибок).</summary>
public sealed record ServiceAnalysis(DiscoveredService Service, string Location, AnalysisResult? Result);

// Общий per-service код-разбор для линз, требующих КОДА (дрейф от свода, нарушения слоёв):
// discovery (только сервисы С compose — у них есть узлы в графе, к которым привяжется location) +
// Фаза-1 анализ каждого сервиса. Единый источник, чтобы линзы не дублировали обход/фильтр/изоляцию.
public sealed class ServiceAnalyzer(IServiceDiscoverySource discovery, ITypeSource types, TypeClassifier classifier)
{
    // Кап параллелизма per-service разбора: Roslyn на сервис тяжёл (память компиляции + CPU). Половина ядер
    // (≥1) — чтобы не пробуксовывать на памяти и оставить машине воздух. Детерминизм не страдает: результаты
    // кладём по индексу, порядок = discovery. Вынесено в тестируемую единицу.
    internal static int MaxParallelism => Math.Max(1, Environment.ProcessorCount / 2);

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
            results[i] = new ServiceAnalysis(svc, location, result);
        });
        return results;
    }
}
