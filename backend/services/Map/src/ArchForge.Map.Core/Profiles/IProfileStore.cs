using ArchForge.Contracts;

namespace ArchForge.Map.Core.Profiles;

public enum ReplaceResult { Ok, NotFound, Conflict }

/// <summary>Хранилище конфигураций анализа и привязок «проект → конфигурация» (adr/0012). Живёт в Map:
/// Analysis остаётся без состояния. Ключ привязки — нормализованный путь проекта.</summary>
public interface IProfileStore
{
    Task<IReadOnlyList<ConfigManifestDto>> ListConfigsAsync(CancellationToken ct = default);
    Task<ConfigManifestDto?> GetConfigAsync(string id, CancellationToken ct = default);
    /// <summary>Добавляет новую конфигурацию (Id уже назначен). false — Id занят.</summary>
    Task<bool> AddConfigAsync(ConfigManifestDto config, CancellationToken ct = default);
    /// <summary>Заменяет конфигурацию, если её текущая версия равна <paramref name="expectedVersion"/>.</summary>
    Task<ReplaceResult> ReplaceConfigAsync(string id, int expectedVersion, ConfigManifestDto replacement, CancellationToken ct = default);
    /// <summary>Удаляет конфигурацию и привязки, которые на неё ссылаются. false — не найдена.</summary>
    Task<bool> DeleteConfigAsync(string id, CancellationToken ct = default);

    Task<ProfileBindingDto?> GetBindingAsync(string normalizedPath, CancellationToken ct = default);
    /// <summary><paramref name="binding"/> = null снимает привязку.</summary>
    Task SetBindingAsync(string normalizedPath, ProfileBindingDto? binding, CancellationToken ct = default);
    Task<int> CountBindingsAsync(CancellationToken ct = default);
}
