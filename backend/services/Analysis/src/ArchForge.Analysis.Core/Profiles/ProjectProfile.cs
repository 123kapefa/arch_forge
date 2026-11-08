namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Пороги, вынесенные из констант детекторов (adr/0012). Значения по умолчанию = прежнему поведению.</summary>
/// <param name="ServiceCycleMinConfidence">Цикл образуют только вызовы не слабее этой уверенности (0..1).</param>
/// <param name="DtoMinSites">DTO собран инлайн не меньше чем в стольких местах — смелл «маппинг не централизован».</param>
/// <param name="DtoMinFiles">…и не меньше чем в стольких файлах.</param>
/// <param name="Naming">Имена сервисов в compose (суффиксы, сателлиты).</param>
public sealed record ProfileParams(
    double ServiceCycleMinConfidence = ProfileParams.DefaultServiceCycleMinConfidence,
    int DtoMinSites = 3,
    int DtoMinFiles = 3,
    ServiceNaming? Naming = null)
{
    public const double DefaultServiceCycleMinConfidence = 0.8;
}

/// <summary>Профиль проекта: уровень + переопределения правил + пороги (adr/0012).
/// Анализатор остаётся без состояния: «путь + профиль → результат». Профиль по умолчанию — Standard без переопределений.</summary>
public sealed record ProjectProfile(
    ProjectLevel Level = ProjectLevel.Standard,
    IReadOnlyDictionary<string, Strictness>? Overrides = null,
    ProfileParams? Params = null,
    IReadOnlyDictionary<string, RoleVocabulary>? Vocabulary = null)
{
    public static ProjectProfile Default { get; } = new();

    /// <summary>Пороги с защитой от мусора: NaN → значение по умолчанию, остальное зажато в [0..1].
    /// (NaN молча отключил бы детектор циклов: любое сравнение с NaN ложно.) Целые зажаты в [1..1000], списки имён очищены.</summary>
    public ServiceNaming Naming => Thresholds.Naming!;

    public ProfileParams Thresholds
    {
        get
        {
            var p = Params ?? new ProfileParams();
            var c = p.ServiceCycleMinConfidence;
            return new ProfileParams(
                double.IsNaN(c) ? ProfileParams.DefaultServiceCycleMinConfidence : Math.Clamp(c, 0, 1),
                Math.Clamp(p.DtoMinSites, 1, 1000),
                Math.Clamp(p.DtoMinFiles, 1, 1000),
                (p.Naming ?? ServiceNaming.Default).Sanitized());
        }
    }

    /// <summary>Строгость правила: переопределение пользователя → умолчание уровня → null (родная серьёзность детектора).
    /// Для неизвестного правила (не из каталога) всегда null.</summary>
    public Strictness? Effective(string ruleId)
    {
        if (RuleCatalog.Find(ruleId) is not { } rule) return null;
        if (Overrides is not null && Overrides.TryGetValue(ruleId, out var o)) return o;
        return rule.DefaultFor(Level);
    }
}
