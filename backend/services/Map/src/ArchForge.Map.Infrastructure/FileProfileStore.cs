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

    // Relaxed-кодировщик: кириллица пишется как есть (а не \uXXXX), файл остаётся компактным и читаемым.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

    public async Task<AddResult> AddConfigAsync(ConfigManifestDto config, int maxConfigs, CancellationToken ct = default) =>
        await Locked(async () =>
        {
            var f = await Load(ct);
            if (f.Configs.Any(c => c.Id == config.Id)) return AddResult.IdTaken;
            if (f.Configs.Count >= maxConfigs) return AddResult.LimitReached;
            f.Configs.Add(config);
            await Save(f, ct);
            return AddResult.Ok;
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

    public async Task ClearBindingAsync(string normalizedPath, CancellationToken ct = default) =>
        await Locked<bool>(async () =>
        {
            var f = await Load(ct);
            if (f.Bindings.Remove(normalizedPath)) await Save(f, ct);
            return true;
        }, ct);

    public async Task<BindResult> TrySetBindingAsync(string normalizedPath, ProfileBindingDto binding, int maxBindings,
        IReadOnlySet<string> builtinIds, CancellationToken ct = default) =>
        await Locked(async () =>
        {
            var f = await Load(ct);
            if (binding.ConfigId is { } id && !builtinIds.Contains(id) && !f.Configs.Any(c => c.Id == id)) return BindResult.ConfigMissing;
            if (!f.Bindings.ContainsKey(normalizedPath) && f.Bindings.Count >= maxBindings) return BindResult.LimitReached;
            f.Bindings[normalizedPath] = binding;
            await Save(f, ct);
            return BindResult.Ok;
        }, ct);

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
            if (f.Version > 1) throw new InvalidDataException($"Файл profiles.json создан более новой версией приложения (формат {f.Version}) и не тронут.") { Data = { ["path"] = _file } };
            // Валидный JSON с пустыми записями не должен ронять сервис: выкидываем null-элементы.
            return f with
            {
                Configs = [.. (f.Configs ?? []).Where(c => c is not null && c.Id is not null)],
                Bindings = new((f.Bindings ?? []).Where(kv => kv.Value is not null), StringComparer.Ordinal),
            };
        }
        catch (JsonException e)
        {
            // В сообщении (оно уходит клиенту) нет полного пути и текста парсера; путь — в Data для лога.
            throw new InvalidDataException("Файл profiles.json в папке данных arch_forge повреждён и не тронут. Исправьте или удалите его вручную.", e) { Data = { ["path"] = _file } };
        }
    }

    private async Task Save(StoreFile f, CancellationToken ct)
    {
        var tmp = $"{_file}.{Guid.NewGuid():N}.tmp";   // уникальное имя: два процесса не пишут в один временный файл
        try
        {
            await using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(s, f, Json, ct);
                await s.FlushAsync(ct);
                s.Flush(flushToDisk: true);            // данные на диске до переименования, иначе при сбое питания файл мог бы опустеть
            }
            File.Move(tmp, _file, overwrite: true);
        }
        catch { try { File.Delete(tmp); } catch { /* уборка best-effort */ } throw; }
    }
}
