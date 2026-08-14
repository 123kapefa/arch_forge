namespace ArchForge.Analysis.Core.Systems;

// Порт: собрать системный граф из корня проекта. Мульти-источник (Фаза 1.5);
// в спайке P1.5-0 реализация читает docker-compose. Реализация — в Infrastructure.Manifests.
public interface ISystemSource
{
    SystemGraph Read(string root, CancellationToken ct = default);
}
