namespace ArchForge.Analysis.Core.Systems;

// Порт: собрать системный граф из корня проекта. Мульти-источник (Фаза 1.5);
// в спайке P1.5-0 реализация читает docker-compose. Реализация — в Infrastructure.Manifests.
public interface ISystemSource
{
    // env — выбранная среда (E9.1 слой 2): null/"default" → base (+override.yml); именованная → base + <env>-оверлей.
    SystemGraph Read(string root, string? env = null, CancellationToken ct = default);
}
