using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Преобразование между контрактом (<see cref="ProfileDto"/>) и доменной моделью профиля (adr/0012).</summary>
public static class ProfileMapping
{
    /// <summary>Идентификаторы правил каталога — для <see cref="ProfileValidator"/>.</summary>
    public static IReadOnlySet<string> KnownRuleIds { get; } =
        RuleCatalog.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Проверяет и преобразует. Ошибки — на русском; при ошибках профиль не создаётся.</summary>
    public static bool TryToProfile(ProfileDto? dto, out ProjectProfile profile, out IReadOnlyList<string> errors)
    {
        errors = ProfileValidator.Validate(dto, KnownRuleIds);
        if (errors.Count > 0 || dto is null) { profile = ProjectProfile.Default; return false; }

        var level = Enum.Parse<ProjectLevel>(dto.Level, ignoreCase: true);
        var overrides = dto.Overrides?.ToDictionary(
            kv => kv.Key, kv => Enum.Parse<Strictness>(kv.Value, ignoreCase: true), StringComparer.Ordinal);

        ProfileParams? prm = null;
        if (dto.Params is { } p)
        {
            var d = new ProfileParams();
            ServiceNaming? naming = null;
            if (p.Naming is { } n)
                naming = new ServiceNaming(
                    n.TypeSuffixes ?? ServiceNaming.Default.TypeSuffixes,
                    n.MigrationSuffixes ?? ServiceNaming.Default.MigrationSuffixes,
                    n.SatelliteMarkers ?? ServiceNaming.Default.SatelliteMarkers);
            prm = new ProfileParams(
                p.ServiceCycleMinConfidence ?? d.ServiceCycleMinConfidence,
                p.DtoMinSites ?? d.DtoMinSites,
                p.DtoMinFiles ?? d.DtoMinFiles,
                naming);
        }

        var vocabulary = dto.Vocabulary?.ToDictionary(
            kv => kv.Key,
            kv => new RoleVocabulary(kv.Value.Markers ?? [], kv.Value.Fallback ?? [], kv.Value.Suffixes ?? []),
            StringComparer.Ordinal);

        profile = new ProjectProfile(level, overrides, prm, vocabulary);
        return true;
    }

    public static RuleCatalogDto ToCatalogDto() => new(
        RuleCatalog.Version,
        [.. RuleCatalog.All.Select(r => new RuleDto(
            r.Id, r.Category, r.Title, r.Note,
            Enum.GetValues<ProjectLevel>().ToDictionary(l => l.ToString(), l => r.DefaultFor(l)?.ToString())))]);
}
