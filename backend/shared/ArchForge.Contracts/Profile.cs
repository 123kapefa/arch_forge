using System.Text.Json.Serialization;

namespace ArchForge.Contracts;

// Контракт профиля проекта (adr/0012). Общий для Analysis, Map и фронта. Все поля, кроме уровня, опциональны:
// отсутствие значения = «как задаёт уровень». Контракт аддитивный: новые поля добавляются опциональными,
// FormatVersion манифеста растёт только при несовместимой смене.

/// <summary>Профиль, который читает Analysis: уровень + переопределения правил + пороги + словарь ролей.</summary>
/// <param name="Level">Simple | Standard | Platform.</param>
/// <param name="Overrides">id правила → Off | Info | Warning | Error. Отсутствие = значение уровня.</param>
/// <param name="Vocabulary">имя роли свода («Command», «Handler»…) → как она называется в коде проекта.</param>
public sealed record ProfileDto(
    string Level,
    IReadOnlyDictionary<string, string>? Overrides = null,
    ProfileParamsDto? Params = null,
    IReadOnlyDictionary<string, RoleVocabularyDto>? Vocabulary = null);

public sealed record ProfileParamsDto(
    double? ServiceCycleMinConfidence = null,
    int? DtoMinSites = null,
    int? DtoMinFiles = null,
    ServiceNamingDto? Naming = null);

public sealed record ServiceNamingDto(
    IReadOnlyList<string>? TypeSuffixes = null,
    IReadOnlyList<string>? MigrationSuffixes = null,
    IReadOnlyList<string>? SatelliteMarkers = null);

public sealed record RoleVocabularyDto(
    IReadOnlyList<string>? Markers = null,
    IReadOnlyList<string>? Fallback = null,
    IReadOnlyList<string>? Suffixes = null);

/// <summary>Конфигурация анализа, хранимая в Map: манифест с автором и версиями (adr/0012).</summary>
/// <param name="Id">Назначает сервер. Встроенные уровни: lvl-simple | lvl-standard | lvl-platform.</param>
/// <param name="Kind">level (встроенный, только чтение) | own (своя). Назначает сервер.</param>
/// <param name="Version">Версия самой конфигурации; растёт при каждом сохранении. Клиент присылает текущую (проверка конфликта).</param>
/// <param name="CatalogVersion">Версия каталога правил, на которой собрана конфигурация. Назначает сервер.</param>
/// <param name="FormatVersion">Версия формата манифеста. Назначает сервер.</param>
/// <param name="ParentId">Конфигурация, с которой сделана копия (для сброса «к родителю»).</param>
public sealed record ConfigManifestDto(
    string? Id,
    string Name,
    string Author,
    int Version,
    ProfileDto Profile,
    string? Kind = null,
    string? CatalogVersion = null,
    int FormatVersion = 1,
    string? ParentId = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>К чему привязан проект: встроенный уровень или своя конфигурация. Ровно одно из двух.</summary>
public sealed record ProfileBindingDto(string? ConfigId = null, string? Level = null);

/// <summary>Ответ GET /api/profile?path=: привязка, сама конфигурация (null для встроенного уровня) и итоговый профиль.</summary>
public sealed record ProfileForPathDto(ProfileBindingDto Binding, ConfigManifestDto? Config, ProfileDto Effective);

/// <summary>Правило каталога: что умеет проверять анализатор и как строго по умолчанию на каждом уровне.</summary>
/// <param name="Defaults">уровень → строгость по умолчанию; null = «родная серьёзность детектора».</param>
public sealed record RuleDto(
    string Id,
    string Category,
    string Title,
    string Note,
    IReadOnlyDictionary<string, string?> Defaults,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RuleExampleDto? Example = null);

/// <summary>Пример «плохо / хорошо» для правила: строки кода (без привязки к проекту).</summary>
public sealed record RuleExampleDto(IReadOnlyList<string> Bad, IReadOnlyList<string> Good);

/// <summary>Сколько находок даёт правило на проекте и проверяется ли оно (false — выключено профилем).</summary>
public sealed record RuleImpactDto(int Count, bool Enabled);

/// <summary>Итог разбора проекта по профилю: находки по строгости и по правилам.</summary>
public sealed record ImpactSummaryDto(int Errors, int Warnings, int Infos, IReadOnlyDictionary<string, RuleImpactDto> Rules);

/// <summary>Совет об уровне проекта по его коду. Только совет: выбор всегда за пользователем.</summary>
public sealed record LevelHintDto(string Level, IReadOnlyList<string> Reasons);

/// <summary>Предпросмотр влияния профиля (adr/0012): что изменится в находках, совет уровня и сколько типов встречается
/// у маркеров и суффиксов словаря.</summary>
/// <param name="Current">Находки по проверяемому профилю.</param>
/// <param name="Baseline">Находки по чистому уровню того же профиля (без отличий) — для сравнения.</param>
/// <param name="MarkerCounts">маркер → число типов проекта, у которых он есть среди базовых типов.</param>
/// <param name="SuffixCounts">суффикс имени → число типов проекта с таким окончанием.</param>
public sealed record PreviewDto(
    ImpactSummaryDto Current,
    ImpactSummaryDto Baseline,
    LevelHintDto Hint,
    IReadOnlyDictionary<string, int> MarkerCounts,
    IReadOnlyDictionary<string, int> SuffixCounts);

public sealed record RuleCatalogDto(string Version, IReadOnlyList<RuleDto> Rules);

/// <summary>Значения по умолчанию, от которых отсчитываются отличия в профиле: словарь ролей встроенного свода,
/// имена сервисов и пороги. Нужны интерфейсу настройки, чтобы показать «что изменено».</summary>
public sealed record VocabularyDefaultsDto(
    IReadOnlyDictionary<string, RoleVocabularyDto> Roles,
    ServiceNamingDto Naming,
    ProfileParamsDto Params);

/// <summary>Единая проверка профиля на входе (Map перед сохранением, Analysis перед применением). Чистая функция.
/// Возвращает список ошибок на русском; пусто = профиль допустим.</summary>
public static class ProfileValidator
{
    public static readonly string[] Levels = ["Simple", "Standard", "Platform"];
    public static readonly string[] Strictnesses = ["Off", "Info", "Warning", "Error"];

    public const int MaxOverrides = 128;
    public const int MaxRoles = 64;
    public const int MaxNameLength = 64;
    public const int MaxItemLength = 128;
    public const int MaxListSize = 64;
    public const int MaxNamingList = 32;
    public const int MaxNamingItemLength = 64;
    public const int MaxConfigNameLength = 100;
    /// <summary>Предел сериализованного профиля. Считается дефолтным кодировщиком (кириллица → \uXXXX), т.е. с запасом:
    /// то, что принял Map, заведомо проходит лимит тела запроса Analysis (256 КБ).</summary>
    public const int MaxProfileBytes = 64 * 1024;

    public static bool IsLevel(string? s) => s is not null && Levels.Contains(s, StringComparer.OrdinalIgnoreCase);
    public static bool IsStrictness(string? s) => s is not null && Strictnesses.Contains(s, StringComparer.OrdinalIgnoreCase);

    /// <param name="knownRuleIds">id правил каталога; null — не проверять принадлежность (структурная проверка).</param>
    public static IReadOnlyList<string> Validate(ProfileDto? profile, IReadOnlySet<string>? knownRuleIds = null)
    {
        var errors = new List<string>();
        if (profile is null) { errors.Add("профиль не задан"); return errors; }
        if (SizeOf(profile) > MaxProfileBytes)
        {
            errors.Add($"профиль слишком большой (не больше {MaxProfileBytes / 1024} КБ)");
            return errors; // дальше разбирать раздутый профиль незачем
        }

        if (!IsLevel(profile.Level)) errors.Add($"неизвестный уровень «{Trim(profile.Level)}»; допустимо: {string.Join(", ", Levels)}");

        if (profile.Overrides is { } ov)
        {
            if (ov.Count > MaxOverrides) errors.Add($"переопределений правил не больше {MaxOverrides}");
            foreach (var (id, s) in ov.Take(MaxOverrides))
            {
                if (knownRuleIds is not null && !knownRuleIds.Contains(id)) errors.Add($"неизвестное правило «{Trim(id)}»");
                if (!IsStrictness(s)) errors.Add($"правило «{Trim(id)}»: неизвестная строгость «{Trim(s)}»; допустимо: {string.Join(", ", Strictnesses)}");
            }
        }

        if (profile.Params is { } p)
        {
            if (p.ServiceCycleMinConfidence is { } c && !(c >= 0 && c <= 1)) errors.Add("порог уверенности цикла должен быть числом от 0 до 1");
            if (p.DtoMinSites is < 1 or > 1000) errors.Add("DtoMinSites должен быть от 1 до 1000");
            if (p.DtoMinFiles is < 1 or > 1000) errors.Add("DtoMinFiles должен быть от 1 до 1000");
            if (p.Naming is { } n)
            {
                CheckNaming("TypeSuffixes", n.TypeSuffixes, errors);
                CheckNaming("MigrationSuffixes", n.MigrationSuffixes, errors);
                CheckNaming("SatelliteMarkers", n.SatelliteMarkers, errors);
            }
        }

        if (profile.Vocabulary is { } voc)
        {
            if (voc.Count > MaxRoles) errors.Add($"ролей в словаре не больше {MaxRoles}");
            foreach (var (role, v) in voc.Take(MaxRoles))
            {
                if (string.IsNullOrWhiteSpace(role) || role.Length > MaxNameLength) errors.Add($"имя роли «{Trim(role)}» должно быть от 1 до {MaxNameLength} символов");
                if (v is null) { errors.Add($"роль «{Trim(role)}»: значение не задано"); continue; }
                CheckList($"роль «{Trim(role)}», маркеры", v.Markers, errors);
                CheckList($"роль «{Trim(role)}», запасные признаки", v.Fallback, errors);
                CheckList($"роль «{Trim(role)}», суффиксы", v.Suffixes, errors);
            }
        }
        return errors;
    }

    /// <summary>Проверка манифеста конфигурации: имя, автор + вложенный профиль.</summary>
    public static IReadOnlyList<string> Validate(ConfigManifestDto? config, IReadOnlySet<string>? knownRuleIds = null)
    {
        if (config is null) return ["конфигурация не задана"];
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(config.Name) || config.Name.Trim().Length > MaxConfigNameLength)
            errors.Add($"название должно быть от 1 до {MaxConfigNameLength} символов");
        if ((config.Author ?? "").Length > MaxConfigNameLength) errors.Add($"автор не длиннее {MaxConfigNameLength} символов");
        if (config.ParentId is { Length: > 64 }) errors.Add("parentId слишком длинный");
        errors.AddRange(Validate(config.Profile, knownRuleIds));
        return errors;
    }

    private static void CheckList(string what, IReadOnlyList<string>? list, List<string> errors)
    {
        if (list is null) return;
        if (list.Count > MaxListSize) errors.Add($"{what}: не больше {MaxListSize} записей");
        if (list.Take(MaxListSize).Any(s => string.IsNullOrWhiteSpace(s) || s.Length > MaxItemLength))
            errors.Add($"{what}: записи от 1 до {MaxItemLength} символов");
    }

    private static void CheckNaming(string what, IReadOnlyList<string>? list, List<string> errors)
    {
        if (list is null) return;
        if (list.Count > MaxNamingList) errors.Add($"{what}: не больше {MaxNamingList} записей");
        if (list.Take(MaxNamingList).Any(s => string.IsNullOrWhiteSpace(s) || s.Length > MaxNamingItemLength))
            errors.Add($"{what}: записи от 1 до {MaxNamingItemLength} символов");
    }

    // NaN/Infinity допускаем при подсчёте размера (их отдельно отвергает проверка порогов), иначе сериализатор бросил бы исключение.
    private static readonly System.Text.Json.JsonSerializerOptions SizeOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    private static int SizeOf(ProfileDto profile) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(profile, SizeOptions).Length;

    // В тексте ошибки показываем не больше 40 символов чужого ввода.
    private static string Trim(string? s) => s is null ? "" : s.Length <= 40 ? s : s[..40] + "…";
}
