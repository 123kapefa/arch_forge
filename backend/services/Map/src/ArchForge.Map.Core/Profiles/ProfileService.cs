using ArchForge.Contracts;
using Microsoft.Extensions.Logging;

namespace ArchForge.Map.Core.Profiles;

/// <summary>Результат операции над профилями: значение или ошибка с HTTP-кодом и списком причин.</summary>
public sealed record Outcome<T>(T? Value, int Status, IReadOnlyList<string> Errors)
{
    public bool Ok => Status is >= 200 and < 300;
    public static Outcome<T> Success(T value, int status = 200) => new(value, status, []);
    public static Outcome<T> Fail(int status, params string[] errors) => new(default, status, errors);
}

/// <summary>Логика конфигураций анализа (adr/0012): проверка, версии, привязки проектов. Эндпоинты — тонкая оболочка над ней.</summary>
public sealed class ProfileService(IProfileStore store, IRuleCatalogProvider catalog, ILogger<ProfileService>? log = null, TimeProvider? time = null)
{
    public const int MaxOwnConfigs = 200;
    public const int MaxBindings = 1000;
    public const int MaxPathLength = 1024;
    public const string DefaultLevel = "Standard";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Встроенные уровни: только чтение, не хранятся.
    private static readonly IReadOnlyList<ConfigManifestDto> Levels =
        [.. ProfileValidator.Levels.Select(l => new ConfigManifestDto(
            "lvl-" + l.ToLowerInvariant(), l, "arch_forge", 1, new ProfileDto(l), Kind: "level"))];

    /// <summary>Ключ проекта: полный путь без хвостового разделителя; симлинк конечной папки разворачивается;
    /// на Windows и macOS (ФС обычно нечувствительны к регистру) регистр сводится к нижнему, иначе один проект получил бы две привязки.
    /// Относительный путь разрешается от рабочей папки Map — для привязки нужен абсолютный путь проекта.</summary>
    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0)) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            if (Directory.Exists(full) && new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                full = target.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (IOException) { /* не удалось развернуть — остаёмся с полным путём */ }
        catch (UnauthorizedAccessException) { }
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
    }

    private static string? CheckPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "path обязателен"
        : path.Length > MaxPathLength ? $"path длиннее {MaxPathLength} символов"
        : path.Contains('\0') ? "path содержит недопустимые символы" : null;

    // ── конфигурации ──

    public async Task<IReadOnlyList<ConfigManifestDto>> ListAsync(CancellationToken ct = default) =>
        [.. Levels, .. (await store.ListConfigsAsync(ct)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];

    public async Task<ConfigManifestDto?> GetAsync(string id, CancellationToken ct = default) =>
        Levels.FirstOrDefault(l => l.Id == id) ?? await store.GetConfigAsync(id, ct);

    public async Task<Outcome<ConfigManifestDto>> CreateAsync(ConfigManifestDto input, CancellationToken ct = default)
    {
        var rules = await catalog.GetAsync(ct);
        if (rules is null) return Outcome<ConfigManifestDto>.Fail(503, "каталог правил недоступен: Analysis не отвечает");
        var errors = ProfileValidator.Validate(input, KnownIds(rules));
        if (errors.Count > 0) return new(default, 400, errors);
        if (input.ParentId is not null && await GetAsync(input.ParentId, ct) is null)
            return Outcome<ConfigManifestDto>.Fail(400, "родительская конфигурация не найдена");

        var created = Stamp(input, Guid.NewGuid().ToString("N"), 1, rules);
        return await store.AddConfigAsync(created, MaxOwnConfigs, ct) switch   // лимит проверяет само хранилище, атомарно с записью
        {
            AddResult.Ok => Outcome<ConfigManifestDto>.Success(created, 201),
            AddResult.LimitReached => Outcome<ConfigManifestDto>.Fail(409, $"конфигураций не больше {MaxOwnConfigs}"),
            _ => Outcome<ConfigManifestDto>.Fail(409, "не удалось создать конфигурацию, повторите"),
        };
    }

    public async Task<Outcome<ConfigManifestDto>> UpdateAsync(string id, ConfigManifestDto input, CancellationToken ct = default)
    {
        if (Levels.Any(l => l.Id == id)) return Outcome<ConfigManifestDto>.Fail(403, "встроенный уровень нельзя менять: создайте свою конфигурацию");
        var rules = await catalog.GetAsync(ct);
        if (rules is null) return Outcome<ConfigManifestDto>.Fail(503, "каталог правил недоступен: Analysis не отвечает");
        var errors = ProfileValidator.Validate(input, KnownIds(rules));
        if (errors.Count > 0) return new(default, 400, errors);
        if (input.Version < 1) return Outcome<ConfigManifestDto>.Fail(400, "укажите version — текущую версию конфигурации (для проверки конфликта)");
        if (input.ParentId is not null && await GetAsync(input.ParentId, ct) is null)
            return Outcome<ConfigManifestDto>.Fail(400, "родительская конфигурация не найдена");

        var updated = Stamp(input, id, input.Version + 1, rules);   // Id берётся из адреса, а не из тела
        return await store.ReplaceConfigAsync(id, input.Version, updated, ct) switch
        {
            ReplaceResult.Ok => Outcome<ConfigManifestDto>.Success(updated),
            ReplaceResult.NotFound => Outcome<ConfigManifestDto>.Fail(404, "конфигурация не найдена"),
            _ => Outcome<ConfigManifestDto>.Fail(409, $"конфигурация изменилась (у вас версия {input.Version}): обновите её и повторите"),
        };
    }

    public async Task<Outcome<bool>> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (Levels.Any(l => l.Id == id)) return Outcome<bool>.Fail(403, "встроенный уровень нельзя удалить");
        return await store.DeleteConfigAsync(id, ct) ? Outcome<bool>.Success(true) : Outcome<bool>.Fail(404, "конфигурация не найдена");
    }

    public async Task<Outcome<ConfigManifestDto>> DuplicateAsync(string id, string? author, CancellationToken ct = default)
    {
        var src = await GetAsync(id, ct);
        if (src is null) return Outcome<ConfigManifestDto>.Fail(404, "конфигурация не найдена");
        var name = src.Name.Length > ProfileValidator.MaxConfigNameLength - 8 ? src.Name[..(ProfileValidator.MaxConfigNameLength - 8)] : src.Name;
        return await CreateAsync(src with { Id = null, Name = "Копия: " + name, Author = author ?? src.Author, Version = 1, ParentId = src.Id, Kind = null }, ct);
    }

    // ── привязка проекта ──

    public async Task<Outcome<ProfileForPathDto>> GetForPathAsync(string path, CancellationToken ct = default)
    {
        if (CheckPath(path) is { } bad) return Outcome<ProfileForPathDto>.Fail(400, bad);
        var key = NormalizePath(path);
        var binding = await store.GetBindingAsync(key, ct);
        if (binding is null) return Outcome<ProfileForPathDto>.Success(DefaultFor());
        var resolved = await ResolveAsync(binding, ct);
        // привязанная конфигурация исчезла — возвращаемся к уровню по умолчанию, но честно показываем это в привязке
        return Outcome<ProfileForPathDto>.Success(resolved ?? DefaultFor());
    }

    public async Task<Outcome<ProfileForPathDto>> BindProjectAsync(string path, ProfileBindingDto? binding, CancellationToken ct = default)
    {
        if (CheckPath(path) is { } bad) return Outcome<ProfileForPathDto>.Fail(400, bad);
        // Пустое тело — ошибка, а не «сброс»: случайный PUT без тела не должен молча снимать привязку.
        if (binding is null) return Outcome<ProfileForPathDto>.Fail(400, "тело обязательно; чтобы сбросить привязку, отправьте {}");
        var key = NormalizePath(path);

        if (binding.ConfigId is null && binding.Level is null)
        {   // {} = «сбросить к умолчанию»
            await store.ClearBindingAsync(key, ct);
            return Outcome<ProfileForPathDto>.Success(DefaultFor());
        }
        if ((binding.ConfigId is null) == (binding.Level is null))
            return Outcome<ProfileForPathDto>.Fail(400, "укажите ровно одно: configId или level");
        if (binding.Level is not null && !ProfileValidator.IsLevel(binding.Level))
            return Outcome<ProfileForPathDto>.Fail(400, $"неизвестный уровень; допустимо: {string.Join(", ", ProfileValidator.Levels)}");
        if (binding.ConfigId is { Length: > 64 }) return Outcome<ProfileForPathDto>.Fail(400, "configId слишком длинный");

        var canonical = binding.Level is { } l ? new ProfileBindingDto(null, Canonical(l)) : binding;
        var builtin = Levels.Select(x => x.Id!).ToHashSet(StringComparer.Ordinal);
        switch (await store.TrySetBindingAsync(key, canonical, MaxBindings, builtin, ct))   // проверки существования и лимита — внутри хранилища
        {
            case BindResult.ConfigMissing: return Outcome<ProfileForPathDto>.Fail(404, "конфигурация не найдена");
            case BindResult.LimitReached: return Outcome<ProfileForPathDto>.Fail(409, $"привязок проектов не больше {MaxBindings}");
        }
        return Outcome<ProfileForPathDto>.Success((await ResolveAsync(canonical, ct)) ?? DefaultFor());
    }

    /// <summary>Профиль для разбора проекта. null — привязки нет, Analysis применяет профиль по умолчанию (прежнее поведение).
    /// Любая ошибка хранилища не ломает разбор: логируем и работаем по умолчанию.</summary>
    public async Task<ProfileDto?> EffectiveForAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (CheckPath(path) is not null) return null;
            var binding = await store.GetBindingAsync(NormalizePath(path), ct);
            return binding is null ? null : (await ResolveAsync(binding, ct))?.Effective;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.LogWarning(e, "Профиль проекта не прочитан — разбор идёт с профилем по умолчанию: {Path}", path);
            return null;
        }
    }

    // ── внутреннее ──

    private static ProfileForPathDto DefaultFor() =>
        new(new ProfileBindingDto(null, DefaultLevel), null, new ProfileDto(DefaultLevel));

    private async Task<ProfileForPathDto?> ResolveAsync(ProfileBindingDto binding, CancellationToken ct)
    {
        if (binding.Level is { } l) return new(new ProfileBindingDto(null, Canonical(l)), null, new ProfileDto(Canonical(l)));
        if (binding.ConfigId is null) return null;
        var cfg = await GetAsync(binding.ConfigId, ct);
        return cfg is null ? null : new(binding, cfg.Kind == "level" ? null : cfg, cfg.Profile);
    }

    private static string Canonical(string level) => ProfileValidator.Levels.First(x => x.Equals(level, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlySet<string> KnownIds(RuleCatalogDto rules) => rules.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

    private ConfigManifestDto Stamp(ConfigManifestDto input, string id, int version, RuleCatalogDto rules) => input with
    {
        Id = id,
        Name = input.Name.Trim(),
        Author = string.IsNullOrWhiteSpace(input.Author) ? "local" : input.Author.Trim(),
        Version = version,
        Kind = "own",
        CatalogVersion = rules.Version,
        FormatVersion = 1,
        Profile = input.Profile with
        {
            Level = Canonical(input.Profile.Level),
            Overrides = input.Profile.Overrides?.ToDictionary(kv => kv.Key, kv => ProfileValidator.Strictnesses.First(x => x.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)), StringComparer.Ordinal),
            CustomRules = input.Profile.CustomRules?.Select(r => r with
            {
                Strictness = ProfileValidator.Strictnesses.First(x => x.Equals(r.Strictness, StringComparison.OrdinalIgnoreCase)),
                Title = r.Title.Trim(),
            }).ToList(),
        },
        UpdatedAt = _time.GetUtcNow(),
    };
}
