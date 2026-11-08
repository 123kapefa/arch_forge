using System.Text.Json;
using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;

namespace ArchForge.Map.Infrastructure;

/// <summary>Хранилище профилей в одном JSON-файле в папке данных пользователя (adr/0012). Запись атомарная
/// (временный файл + замена), доступ внутри процесса сериализован. Повреждённый файл НЕ затирается:
/// чтение падает с понятной ошибкой, данные пользователя остаются на диске для ручного разбора.</summary>
public sealed class FileProfileStore : IProfileStore
{
    private sealed record StoreFile(int Version, List<ConfigManifestDto> Configs, Dictionary<string, ProfileBindingDto> Bindings);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileProfileStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "profiles.json");
    }

    /// <summary>Папка по умолчанию: данные приложения пользователя (macOS: ~/Library/Application Support/arch_forge).</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "arch_forge");

    public async Task<IReadOnlyList<ConfigManifestDto>> ListConfigsAsync(CancellationToken ct = default) =>
        await Locked(async () => (IReadOnlyList<ConfigManifestDto>)[.. (await Load(ct)).Configs], ct);

    public async Task<ConfigManifestDto?> GetConfigAsync(string id, CancellationToken ct = default) =>
        await Locked(async () => (await Load(ct)).Configs.FirstOrDefault(c => c.Id == id), ct);

    public async Task<bool> AddConfigAsync(ConfigManifestDto config, CancellationToken ct = default) =>
        await Locked(async () =>
        {
            var f = await Load(ct);
            if (f.Configs.Any(c => c.Id == config.Id)) return false;
            f.Configs.Add(config);
            await Save(f, ct);
            return true;
        }, ct);

    public async Task<ReplaceResult> ReplaceConfigAsync(string id, int expectedVersion, ConfigManifestDto replacement, CancellationToken ct = default) =>
        await Locked(async () =>
        {
            var f = await Load(ct);
            var i = f.Configs.FindIndex(c => c.Id == id);
            if (i < 0) return ReplaceResult.NotFound;
            if (f.Configs[i].Version != expectedVersion) return ReplaceResult.Conflict;
            f.Configs[i] = replacement;
            await Save(f, ct);
            return ReplaceResult.Ok;
        }, ct);

    public async Task<bool> DeleteConfigAsync(string id, CancellationToken ct = default) =>
        await Locked(async () =>
        {
            var f = await Load(ct);
            if (f.Configs.RemoveAll(c => c.Id == id) == 0) return false;
            foreach (var key in f.Bindings.Where(kv => kv.Value.ConfigId == id).Select(kv => kv.Key).ToList())
                f.Bindings.Remove(key); // привязки к удалённой конфигурации снимаем: проект вернётся к уровню по умолчанию
            await Save(f, ct);
            return true;
        }, ct);

    public async Task<ProfileBindingDto?> GetBindingAsync(string normalizedPath, CancellationToken ct = default) =>
        await Locked(async () => (await Load(ct)).Bindings.GetValueOrDefault(normalizedPath), ct);

    public async Task SetBindingAsync(string normalizedPath, ProfileBindingDto? binding, CancellationToken ct = default) =>
        await Locked<bool>(async () =>
        {
            var f = await Load(ct);
            if (binding is null) f.Bindings.Remove(normalizedPath); else f.Bindings[normalizedPath] = binding;
            await Save(f, ct);
            return true;
        }, ct);

    public async Task<int> CountBindingsAsync(CancellationToken ct = default) =>
        await Locked(async () => (await Load(ct)).Bindings.Count, ct);

    // ── файл ──

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    private async Task<StoreFile> Load(CancellationToken ct)
    {
        if (!File.Exists(_file)) return new StoreFile(1, [], new(StringComparer.Ordinal));
        try
        {
            await using var s = File.OpenRead(_file);
            var f = await JsonSerializer.DeserializeAsync<StoreFile>(s, Json, ct)
                    ?? throw new InvalidDataException("файл пуст");
            return f with { Configs = f.Configs ?? [], Bindings = new(f.Bindings ?? [], StringComparer.Ordinal) };
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Файл профилей повреждён и не тронут: {_file}. Исправьте или удалите его вручную. ({e.Message})", e);
        }
    }

    private async Task Save(StoreFile f, CancellationToken ct)
    {
        var tmp = _file + ".tmp";
        await using (var s = File.Create(tmp))
            await JsonSerializer.SerializeAsync(s, f, Json, ct);
        File.Move(tmp, _file, overwrite: true);
    }
}
