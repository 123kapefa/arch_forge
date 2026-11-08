using ArchForge.Contracts;

namespace ArchForge.Map.Core.Profiles;

public enum ReplaceResult { Ok, NotFound, Conflict }
public enum AddResult { Ok, IdTaken, LimitReached }
public enum BindResult { Ok, ConfigMissing, LimitReached }

/// <summary>Хранилище конфигураций анализа и привязок «проект → конфигурация» (adr/0012). Живёт в Map:
/// Analysis остаётся без состояния. Ключ привязки — нормализованный путь проекта.</summary>
public interface IProfileStore
{
    Task<IReadOnlyList<ConfigManifestDto>> ListConfigsAsync(CancellationToken ct = default);
    Task<ConfigManifestDto?> GetConfigAsync(string id, CancellationToken ct = default);
    /// <summary>Добавляет новую конфигурацию (Id уже назначен). Лимит числа конфигураций проверяется в том же захвате,
    /// что и запись, поэтому параллельные вызовы его не превысят.</summary>
    Task<AddResult> AddConfigAsync(ConfigManifestDto config, int maxConfigs, CancellationToken ct = default);
    /// <summary>Заменяет конфигурацию, если её текущая версия равна <paramref name="expectedVersion"/>.</summary>
    Task<ReplaceResult> ReplaceConfigAsync(string id, int expectedVersion, ConfigManifestDto replacement, CancellationToken ct = default);
    /// <summary>Удаляет конфигурацию и привязки, которые на неё ссылаются. false — не найдена.</summary>
    Task<bool> DeleteConfigAsync(string id, CancellationToken ct = default);

    Task<ProfileBindingDto?> GetBindingAsync(string normalizedPath, CancellationToken ct = default);
    /// <summary>Снимает привязку проекта (если была).</summary>
    Task ClearBindingAsync(string normalizedPath, CancellationToken ct = default);
    /// <summary>Привязывает проект. Существование конфигурации (кроме встроенных <paramref name="builtinIds"/>) и лимит привязок
    /// проверяются в том же захвате, что и запись: привязка не повиснет на только что удалённой конфигурации.</summary>
    Task<BindResult> TrySetBindingAsync(string normalizedPath, ProfileBindingDto binding, int maxBindings, IReadOnlySet<string> builtinIds, CancellationToken ct = default);
}
