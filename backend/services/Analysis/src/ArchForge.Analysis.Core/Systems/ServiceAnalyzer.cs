using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

/// <summary>Результат per-service код-разбора; <c>Result == null</c> — сервис не разобрался (изоляция ошибок).</summary>
public sealed record ServiceAnalysis(DiscoveredService Service, string Location, AnalysisResult? Result);

// Общий per-service код-разбор для линз, требующих КОДА (дрейф от свода, нарушения слоёв):
// discovery (только сервисы С compose — у них есть узлы в графе, к которым привяжется location) +
// Фаза-1 анализ каждого сервиса. Единый источник, чтобы линзы не дублировали обход/фильтр/изоляцию.
public sealed class ServiceAnalyzer(IServiceDiscoverySource discovery, ITypeSource types, TypeClassifier classifier)
{
    public IReadOnlyList<ServiceAnalysis> Analyze(string root, ConventionPack pack, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return []; // код-разбор идёт по коду сервисов — нужен корень-папка

        var list = new List<ServiceAnalysis>();
        foreach (var svc in discovery.Discover(root).Where(s => s.ComposePath is not null))
        {
            ct.ThrowIfCancellationRequested();
            var location = SystemNamespace.Of(root, svc.Root); // namespace-префикс узлов сервиса ({system})
            AnalysisResult? result;
            try { result = classifier.Classify(types.Read(svc.Root, ct), pack); }
            catch (OperationCanceledException) { throw; }
            catch { result = null; } // сервис не разобрался — не роняем скан (adr/0008); линза поднимет Coverage
            list.Add(new ServiceAnalysis(svc, location, result));
        }
        return list;
    }
}
