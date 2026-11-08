namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Пороги, вынесенные из констант детекторов (adr/0012). Значения по умолчанию = прежнему поведению.</summary>
/// <param name="ServiceCycleMinConfidence">Цикл образуют только вызовы не слабее этой уверенности (0..1).</param>
public sealed record ProfileParams(double ServiceCycleMinConfidence = ProfileParams.DefaultServiceCycleMinConfidence)
{
    public const double DefaultServiceCycleMinConfidence = 0.8;
}

/// <summary>Профиль проекта: уровень + переопределения правил + пороги (adr/0012).
/// Анализатор остаётся без состояния: «путь + профиль → результат». Профиль по умолчанию — Standard без переопределений.</summary>
public sealed record ProjectProfile(
    ProjectLevel Level = ProjectLevel.Standard,
    IReadOnlyDictionary<string, Strictness>? Overrides = null,
    ProfileParams? Params = null)
{
    public static ProjectProfile Default { get; } = new();

    public ProfileParams Thresholds => Params ?? new ProfileParams();

    /// <summary>Строгость правила: переопределение пользователя → умолчание уровня → null (родная серьёзность детектора).
    /// Для неизвестного правила (не из каталога) всегда null.</summary>
    public Strictness? Effective(string ruleId)
    {
        if (RuleCatalog.Find(ruleId) is not { } rule) return null;
        if (Overrides is not null && Overrides.TryGetValue(ruleId, out var o)) return o;
        return rule.DefaultFor(Level);
    }
}
