using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core.Systems;

// Линза «дрейф от свода»: по каждому найденному сервису (.sln) гоняем Фаза-1 анализ (ITypeSource + TypeClassifier)
// и поднимаем отклонения от свода на системный уровень как проблемы категории convention, severity Info.
// Мягко (не красный алерт): на ЧУЖОМ таргете дрейф — это «другие конвенции», а не арх-проблема, поэтому
// отдельная линза, включаемая по запросу (adr/0008 — не выдаём конвенц-различия за проблемы).
// Гранулярность — уровень найденного сервиса (.sln): моно-репо (одно решение на много сервисов) анализируется целиком.
public sealed class DriftScanner(IServiceDiscoverySource discovery, ITypeSource types, TypeClassifier classifier)
{
    public IReadOnlyList<SystemProblem> Scan(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return []; // дрейф гоняется по коду сервисов — нужен корень-папка

        var problems = new List<SystemProblem>();
        // только сервисы С compose: у них есть узлы в системном графе, к которым привяжется location.
        // «Голый» .sln без compose узлов не даёт → его дрейф был бы осиротевшей проблемой (ревью #22 M4).
        foreach (var svc in discovery.Discover(root).Where(s => s.ComposePath is not null))
        {
            ct.ThrowIfCancellationRequested();
            // location = namespace-префикс узлов этого сервиса ({system}) → фронт привяжет к его узлам
            var location = SystemNamespace.Of(root, svc.Root);

            AnalysisResult result;
            try { result = classifier.Classify(types.Read(svc.Root, ct)); }
            catch (OperationCanceledException) { throw; }
            catch // сервис не разобрался — НЕ молчим (adr/0008): честная Coverage-проблема в той же линзе
            {
                problems.Add(new SystemProblem(ProblemSeverity.Coverage, "drift-scan-failed",
                    $"«{svc.Name}»: не удалось разобрать код для дрейфа — пропущен", location, ProblemCategory.Convention));
                continue;
            }

            if (result.Drift.Count == 0) continue;

            var total = result.Coverage.Marker + result.Coverage.Heuristic + result.Coverage.Unknown;
            var markerPct = total > 0 ? result.Coverage.Marker * 100 / total : 0;
            var examples = string.Join("; ", result.Drift.Take(3));
            problems.Add(new SystemProblem(
                ProblemSeverity.Info, "drift",
                $"«{svc.Name}»: {result.Drift.Count} расхождений со сводом (маркеров {markerPct}%). Напр.: {examples}",
                location, ProblemCategory.Convention));
        }
        return problems;
    }
}
