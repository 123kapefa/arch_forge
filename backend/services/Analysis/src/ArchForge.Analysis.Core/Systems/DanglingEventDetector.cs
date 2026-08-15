namespace ArchForge.Analysis.Core.Systems;

// Детектор проблем событийного потока вокруг Topic-узлов (шин).
//
// ЧЕСТНОСТЬ (adr/0008): «висячее событие» и «осиротевшая подписка» требуют ЗНАНИЯ НАПРАВЛЕНИЯ —
// кто publisher, кто subscriber. Направление даёт только разбор кода (рёбра publishes/subscribes),
// а не compose: «uses-bus» = «подключается к шине» с НЕИЗВЕСТНЫМ направлением. Поэтому:
//   • publishes без subscribes  → dangling-event      (публикуют, никто не читает)   — Warning;
//   • subscribes без publishes   → orphaned-subscription (ждут событий, которых нет)  — Warning;
//   • только uses-bus            → направление неизвестно → МОЛЧИМ (не выдумываем dangling);
//   • топик вообще без рёбер      → isolated-topic — не используется ИЛИ связь в коде (не разобрано) — Coverage.
//
// Пока направление pub/sub не извлекается из кода, dangling/orphaned честно молчат на compose-таргетах
// (там только uses-bus). Каркас готов и активируется, как только появятся ориентированные рёбра.
public static class DanglingEventDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph)
    {
        var problems = new List<SystemProblem>();
        foreach (var topic in graph.Nodes.Where(n => n.Kind == SystemNodeKind.Topic))
        {
            var touching = graph.Edges.Where(e => e.Source == topic.Id || e.Target == topic.Id).ToList();
            var publishers = touching.Count(e => e is { Kind: "publishes", Deferred: false });
            var subscribers = touching.Count(e => e is { Kind: "subscribes", Deferred: false });

            if (touching.Count == 0)
                problems.Add(new SystemProblem(ProblemSeverity.Coverage, "isolated-topic",
                    $"шина «{topic.Name}» без подключений — не используется или связь объявлена в коде (не разобрано)",
                    topic.Id));
            else if (publishers > 0 && subscribers == 0)
                problems.Add(new SystemProblem(ProblemSeverity.Warning, "dangling-event",
                    $"в «{topic.Name}» публикуют ({publishers}), но подписчиков не найдено", topic.Id));
            else if (subscribers > 0 && publishers == 0)
                problems.Add(new SystemProblem(ProblemSeverity.Warning, "orphaned-subscription",
                    $"на «{topic.Name}» подписаны ({subscribers}), но публикаций не найдено", topic.Id));
            // publishers>0 && subscribers>0 → здоровый поток; только uses-bus → направление неизвестно, молчим
        }
        return problems;
    }
}
