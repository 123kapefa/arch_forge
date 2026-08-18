using System.Text.Json;

namespace ArchForge.Analysis.Core;

// Convention-pack: маркер (базовый тип/интерфейс) → роль. Источник — свод команды.
// Грузится из файла проекта (archforge.conventions.json), иначе — встроенный дефолт.
// Правило свод-линзы «маппинг не централизован»: какие типы считать DTO (Suffix), какие места считать
// «централизаторами» (CentralizeIn — по суффиксу имени: Mapper/Mapping/…), и пороги для смелла.
public sealed record DtoMappingRule(
    IReadOnlyList<string> Suffix,
    IReadOnlyList<string> CentralizeIn,
    int MinSites,
    int MinFiles)
{
    public static readonly DtoMappingRule Default =
        new(["Dto"], ["Mapper", "Mapping", "Builder", "Factory", "Extensions"], MinSites: 3, MinFiles: 3);
}

public sealed class ConventionPack(
    IReadOnlyDictionary<string, string> markers,
    IReadOnlyDictionary<string, string> fallback,
    IReadOnlyList<(string Suffix, string Marker, string Role)> expect,
    string source = "встроенный свод",
    DtoMappingRule? dtoMapping = null)
{
    public IReadOnlyDictionary<string, string> Markers { get; } = markers;
    public IReadOnlyDictionary<string, string> Fallback { get; } = fallback;
    public IReadOnlyList<(string Suffix, string Marker, string Role)> Expect { get; } = expect;
    public DtoMappingRule DtoMapping { get; } = dtoMapping ?? DtoMappingRule.Default;
    public string Source { get; } = source; // "встроенный свод" | "файл: <имя>" | "ошибка … → встроенный"

    // Дефолт: маркеры свода + популярные .NET-паттерны. Пользовательский пак заменит/дополнит.
    public static ConventionPack Default() => new(
        markers: new Dictionary<string, string>
        {
            ["ICommand"] = "Command", ["IQuery"] = "Query",
            ["ICommandHandler"] = "Handler", ["IQueryHandler"] = "Handler", ["IQueryHandlerWithResult"] = "Handler",
            ["AbstractValidator"] = "Validator", ["IEndpoint"] = "Endpoint",
            ["Aggregate"] = "Aggregate", ["IDomainEvent"] = "DomainEvent",
        },
        fallback: new Dictionary<string, string>
        {
            ["IdentityUser"] = "Entity (Identity)", ["IdentityRole"] = "Role (Identity)",
            ["IdentityDbContext"] = "DbContext", ["DbContext"] = "DbContext",
            ["PageModel"] = "Razor Page", ["ControllerBase"] = "Controller",
            ["Migration"] = "EF migration", ["ModelSnapshot"] = "EF snapshot",
            ["IEntityTypeConfiguration"] = "EF config", ["IDesignTimeDbContextFactory"] = "EF factory",
            ["IHostedService"] = "Hosted service", ["BackgroundService"] = "Hosted service",
            ["IOpenIddictServerHandler"] = "OIDC handler",
            ["OpenIddictEntityFrameworkCoreApplication"] = "OIDC entity",
            ["OpenIddictEntityFrameworkCoreAuthorization"] = "OIDC entity",
            ["OpenIddictEntityFrameworkCoreScope"] = "OIDC entity",
            ["OpenIddictEntityFrameworkCoreToken"] = "OIDC entity",
            ["Exception"] = "Exception", ["Attribute"] = "Attribute",
        },
        expect: new (string, string, string)[]
        {
            ("Command", "ICommand", "Command"),
            ("Handler", "ICommandHandler/IQueryHandler", "Handler"),
            ("Validator", "AbstractValidator", "Validator"),
            ("Endpoint", "IEndpoint", "Endpoint"),
        });

    // Имена свод-файла в корне проекта (первый найденный — используется).
    private static readonly string[] FileNames = { "archforge.conventions.json", Path.Combine(".archforge", "conventions.json") };

    /// <summary>Свод из файла проекта (мерж поверх дефолта: пользователь добавляет/переопределяет).
    /// Файл ищем от пути анализа ВВЕРХ по дереву (drill в подпапку сервиса тоже видит свод в корне);
    /// файла нет → дефолт; ошибка чтения → дефолт с пометкой в Source.</summary>
    public static ConventionPack Load(string startPath)
    {
        string? file = null;
        var dir = startPath;
        for (var depth = 0; depth < 12 && !string.IsNullOrEmpty(dir); depth++)
        {
            file = FileNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
            if (file is not null) break;
            dir = Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
        }
        if (file is null) return Default();
        try
        {
            var pf = JsonSerializer.Deserialize<PackFile>(File.ReadAllText(file), JsonOpts);
            if (pf is null) return Default();
            var def = Default();
            var markers = new Dictionary<string, string>(def.Markers, StringComparer.Ordinal);
            if (pf.Markers is not null) foreach (var kv in pf.Markers) markers[kv.Key] = kv.Value;
            var fallback = new Dictionary<string, string>(def.Fallback, StringComparer.Ordinal);
            if (pf.Fallback is not null) foreach (var kv in pf.Fallback) fallback[kv.Key] = kv.Value;
            var expect = new List<(string Suffix, string Marker, string Role)>(def.Expect);
            if (pf.Expect is not null)
                foreach (var r in pf.Expect)
                {
                    expect.RemoveAll(e => e.Suffix == r.Suffix); // тот же суффикс → правило пользователя заменяет
                    expect.Add((r.Suffix, r.Marker, r.Role));
                }
            var dm = DtoMappingRule.Default;
            if (pf.DtoMapping is { } d) // секция dtoMapping: переопределяем поля пофайлово (null → дефолт)
                dm = new DtoMappingRule(
                    d.Suffix ?? dm.Suffix, d.CentralizeIn ?? dm.CentralizeIn,
                    d.MinSites ?? dm.MinSites, d.MinFiles ?? dm.MinFiles);
            return new ConventionPack(markers, fallback, expect, $"файл: {Path.GetFileName(file)}", dm);
        }
        catch
        {
            var d = Default();
            return new ConventionPack(d.Markers, d.Fallback, d.Expect, $"ошибка чтения {Path.GetFileName(file)} → встроенный");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Форма файла свода. Все секции опциональны — файл может лишь дополнять дефолт.
    private sealed record PackFile(
        Dictionary<string, string>? Markers,
        Dictionary<string, string>? Fallback,
        List<ExpectRule>? Expect,
        DtoMappingFile? DtoMapping);

    private sealed record ExpectRule(string Suffix, string Marker, string Role);

    private sealed record DtoMappingFile(
        List<string>? Suffix, List<string>? CentralizeIn, int? MinSites, int? MinFiles);
}
