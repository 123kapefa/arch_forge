using ArchForge.Analysis.Core.Profiles;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Tests.Profiles;

public class ProfileContractTests
{
    private static IReadOnlySet<string> Known => ProfileMapping.KnownRuleIds;

    [Fact]
    public void Minimal_profile_is_valid()
    {
        Assert.Empty(ProfileValidator.Validate(new ProfileDto("Standard"), Known));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("PLATFORM")]
    public void Level_is_case_insensitive(string level)
    {
        Assert.Empty(ProfileValidator.Validate(new ProfileDto(level), Known));
    }

    [Fact]
    public void Unknown_level_unknown_rule_and_bad_strictness_are_reported()
    {
        var dto = new ProfileDto("Huge", new Dictionary<string, string> { ["no-such-rule"] = "Error", ["drift"] = "Loud" });

        var errors = ProfileValidator.Validate(dto, Known);

        Assert.Contains(errors, e => e.Contains("Huge"));
        Assert.Contains(errors, e => e.Contains("no-such-rule"));
        Assert.Contains(errors, e => e.Contains("Loud"));
    }

    [Fact]
    public void Rule_membership_is_not_checked_without_known_ids()
    {
        var dto = new ProfileDto("Simple", new Dictionary<string, string> { ["any"] = "Off" });

        Assert.Empty(ProfileValidator.Validate(dto, knownRuleIds: null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Confidence_out_of_range_or_NaN_is_rejected(double c)
    {
        var dto = new ProfileDto("Standard", Params: new ProfileParamsDto(ServiceCycleMinConfidence: c));

        Assert.NotEmpty(ProfileValidator.Validate(dto, Known));
    }

    [Fact]
    public void Dto_thresholds_out_of_range_are_rejected()
    {
        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Params: new ProfileParamsDto(DtoMinSites: 0)), Known));
        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Params: new ProfileParamsDto(DtoMinFiles: 5000)), Known));
    }

    [Fact]
    public void Oversized_inputs_are_rejected()
    {
        var manyRoles = Enumerable.Range(0, ProfileValidator.MaxRoles + 1)
            .ToDictionary(i => "R" + i, _ => new RoleVocabularyDto(["M"]));
        var manyOverrides = Enumerable.Range(0, ProfileValidator.MaxOverrides + 1).ToDictionary(i => "r" + i, _ => "Off");
        var longName = new Dictionary<string, RoleVocabularyDto> { [new string('x', 200)] = new(["M"]) };
        var longMarker = new Dictionary<string, RoleVocabularyDto> { ["Command"] = new([new string('m', 500)]) };
        var bigNaming = new ProfileParamsDto(Naming: new ServiceNamingDto(TypeSuffixes: [.. Enumerable.Range(0, 100).Select(i => "-s" + i)]));

        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Vocabulary: manyRoles)));
        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Overrides: manyOverrides)));
        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Vocabulary: longName)));
        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Vocabulary: longMarker)));
        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Params: bigNaming)));
    }

    [Fact]
    public void Null_vocabulary_value_and_null_profile_are_rejected_not_thrown()
    {
        var voc = new Dictionary<string, RoleVocabularyDto> { ["Command"] = null! };

        Assert.NotEmpty(ProfileValidator.Validate(new ProfileDto("Standard", Vocabulary: voc)));
        Assert.NotEmpty(ProfileValidator.Validate((ProfileDto?)null));
        Assert.NotEmpty(ProfileValidator.Validate((ConfigManifestDto?)null));
    }

    [Fact]
    public void Error_text_does_not_echo_long_input()
    {
        var errors = ProfileValidator.Validate(new ProfileDto(new string('z', 5000)));

        Assert.All(errors, e => Assert.True(e.Length < 300));
    }

    [Fact]
    public void Manifest_name_and_author_are_checked()
    {
        var p = new ProfileDto("Standard");

        Assert.NotEmpty(ProfileValidator.Validate(new ConfigManifestDto(null, "  ", "a", 1, p)));
        Assert.NotEmpty(ProfileValidator.Validate(new ConfigManifestDto(null, new string('n', 101), "a", 1, p)));
        Assert.Empty(ProfileValidator.Validate(new ConfigManifestDto(null, "Для клиента Альфа", "@kapefa", 1, p), Known));
    }

    // ── ProfileMapping ──

    [Fact]
    public void Mapping_converts_dto_to_domain_profile()
    {
        var dto = new ProfileDto("simple",
            new Dictionary<string, string> { ["drift"] = "warning" },
            new ProfileParamsDto(ServiceCycleMinConfidence: 0.9, DtoMinSites: 5,
                Naming: new ServiceNamingDto(TypeSuffixes: ["-core"])),
            new Dictionary<string, RoleVocabularyDto> { ["Command"] = new(["IRequest"]) });

        Assert.True(ProfileMapping.TryToProfile(dto, out var profile, out var errors));

        Assert.Empty(errors);
        Assert.Equal(ProjectLevel.Simple, profile.Level);
        Assert.Equal(Strictness.Warning, profile.Effective("drift"));      // переопределение сильнее умолчания Simple (Off)
        Assert.Equal(0.9, profile.Thresholds.ServiceCycleMinConfidence);
        Assert.Equal(5, profile.Thresholds.DtoMinSites);
        Assert.Equal(3, profile.Thresholds.DtoMinFiles);                   // не задано → умолчание
        Assert.Equal(["-core"], profile.Naming.TypeSuffixes);
        Assert.Equal(ServiceNaming.Default.MigrationSuffixes, profile.Naming.MigrationSuffixes);
        Assert.Equal(["IRequest"], profile.Vocabulary!["Command"].Markers);
    }

    [Fact]
    public void Mapping_rejects_invalid_profile_and_returns_default()
    {
        var ok = ProfileMapping.TryToProfile(new ProfileDto("Nope"), out var profile, out var errors);

        Assert.False(ok);
        Assert.NotEmpty(errors);
        Assert.Same(ProjectProfile.Default, profile);
    }

    [Fact]
    public void Catalog_dto_exposes_all_rules_with_defaults_per_level()
    {
        var dto = ProfileMapping.ToCatalogDto();

        Assert.Equal(RuleCatalog.Version, dto.Version);
        Assert.Equal(RuleCatalog.All.Count, dto.Rules.Count);
        var drift = dto.Rules.Single(r => r.Id == "drift");
        Assert.Equal("Off", drift.Defaults["Simple"]);
        Assert.Null(drift.Defaults["Standard"]);                           // null = родная серьёзность
        Assert.Equal(["Simple", "Standard", "Platform"], drift.Defaults.Keys.OrderBy(k => Array.IndexOf(ProfileValidator.Levels, k)));
    }
}
