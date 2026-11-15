namespace ArchForge.Analysis.Core.Systems;

// Агрегатор системных детекторов проблем поверх собранного графа (дифференциатор Фазы 1.5 — «что не так»).
// Сейчас: общая БД/хранилище (SharedDbDetector) + цикл сервисов (ServiceCycleDetector) +
// событийный поток (DanglingEventDetector: isolated-topic сейчас; dangling/orphaned — при рёбрах pub/sub из кода) +
// публикация хранилища наружу (ExposedPortDetector, E9.2 — на данных DeployInfo из compose).
// Рёбра pub/sub из кода добавляет PubSubGraphBuilder (#61) и пересчитывает dangling/orphaned.
// дрейф/слои/покрытие из Фазы 1 (нужны per-service тип-графы в системном IR).
public static class SystemProblemDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph, PortSourceLookup? portSource = null) =>
    [
        .. SharedDbDetector.Detect(graph),
        .. ServiceCycleDetector.Detect(graph),
        .. DanglingEventDetector.Detect(graph),
        .. ExposedPortDetector.Detect(graph, locate: portSource),
    ];
}
