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
        foreach (var svc in discovery.Discover(root))
        {
            ct.ThrowIfCancellationRequested();
            AnalysisResult result;
            try { result = classifier.Classify(types.Read(svc.Root, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { continue; } // сервис не разобрался — пропускаем (изоляция ошибок, adr/0008)

            if (result.Drift.Count == 0) continue;

            // location = namespace-префикс узлов этого сервиса ({system}) → фронт сможет привязать к его узлам
            var location = Path.GetRelativePath(root, svc.Root).Replace('\\', '/');
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
