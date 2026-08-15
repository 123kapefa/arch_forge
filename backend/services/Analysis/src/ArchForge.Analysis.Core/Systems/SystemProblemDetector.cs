namespace ArchForge.Analysis.Core.Systems;

// Агрегатор системных детекторов проблем поверх собранного графа (дифференциатор Фазы 1.5 — «что не так»).
// Сейчас: общая БД/хранилище (SharedDbDetector) + цикл сервисов (ServiceCycleDetector).
// Отложено (backlog): висячее событие (нужен subscribe-детект, P1.5-D слайс 2);
// дрейф/слои/покрытие из Фазы 1 (нужны per-service тип-графы в системном IR).
public static class SystemProblemDetector
{
    public static IReadOnlyList<SystemProblem> Detect(SystemGraph graph) =>
        [.. SharedDbDetector.Detect(graph), .. ServiceCycleDetector.Detect(graph)];
}
