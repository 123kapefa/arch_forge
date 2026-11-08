using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Profiles;

public class ProjectProfileTests
{
    private sealed class FakeSource(SystemGraph graph) : ISystemSource
    {
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default) => graph;
    }

    private static SystemProblem Problem(string kind, ProblemSeverity sev = ProblemSeverity.Warning,
        string category = ProblemCategory.Architecture) => new(sev, kind, kind, "loc", category);

    private static SystemNode Svc(string id) => new(id, id, SystemNodeKind.Service, id + ":1");

    // ── профиль и каталог ──

    [Fact]
    public void Default_profile_is_Standard_without_overrides()
    {
        Assert.Equal(ProjectLevel.Standard, ProjectProfile.Default.Level);
        Assert.Null(ProjectProfile.Default.Overrides);
        Assert.Equal(0.8, ProjectProfile.Default.Thresholds.ServiceCycleMinConfidence);
    }

    [Theory]
    [InlineData(ProjectLevel.Standard)]
    [InlineData(ProjectLevel.Platform)]
    public void Standard_and_Platform_keep_native_severity_for_every_rule(ProjectLevel level)
    {
        // введение профилей не должно менять вывод существующих проектов
        var profile = new ProjectProfile(level);
        foreach (var rule in RuleCatalog.All) Assert.Null(profile.Effective(rule.Id));
    }

    [Fact]
    public void Simple_turns_off_convention_lenses_only()
    {
        var profile = new ProjectProfile(ProjectLevel.Simple);

        Assert.Equal(Strictness.Off, profile.Effective("drift"));
        Assert.Equal(Strictness.Off, profile.Effective("dto-mapping"));
        Assert.Null(profile.Effective("service-cycle"));
        Assert.Equal(["drift", "dto-mapping"], ProfileApplier.DisabledRules(profile));
    }

    [Fact]
    public void Override_beats_level_default()
    {
        var profile = new ProjectProfile(ProjectLevel.Simple,
            new Dictionary<string, Strictness> { ["drift"] = Strictness.Warning });

        Assert.Equal(Strictness.Warning, profile.Effective("drift"));
    }

    [Fact]
    public void Unknown_rule_has_no_strictness()
    {
        var profile = new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["no-such"] = Strictness.Off });

        Assert.Null(profile.Effective("no-such")); // правила нет в каталоге → профиль его не трогает
    }

    [Fact]
    public void Catalog_ids_are_unique_and_found()
    {
        Assert.Equal(RuleCatalog.All.Count, RuleCatalog.All.Select(r => r.Id).Distinct().Count());
        Assert.All(RuleCatalog.All, r => Assert.Same(r, RuleCatalog.Find(r.Id)));
    }

    // ── ProfileApplier ──

    [Fact]
    public void Applier_drops_disabled_and_remaps_overridden_and_keeps_the_rest()
    {
        var problems = new[]
        {
            Problem("drift", ProblemSeverity.Info, ProblemCategory.Convention),     // выключено на Simple
            Problem("layer-violation", ProblemSeverity.Warning),                     // переопределено → Error
            Problem("service-cycle", ProblemSeverity.Critical),                      // без изменений
            Problem("layer-scan-failed", ProblemSeverity.Coverage),                  // не из каталога
        };
        var profile = new ProjectProfile(ProjectLevel.Simple,
            new Dictionary<string, Strictness> { ["layer-violation"] = Strictness.Error });

        var result = ProfileApplier.Apply(problems, profile);

        Assert.DoesNotContain(result, p => p.Kind == "drift");
        Assert.Equal(ProblemSeverity.Critical, result.Single(p => p.Kind == "layer-violation").Severity);
        Assert.Equal(ProblemSeverity.Critical, result.Single(p => p.Kind == "service-cycle").Severity);
        Assert.Equal(ProblemSeverity.Coverage, result.Single(p => p.Kind == "layer-scan-failed").Severity);
    }

    [Theory]
    [InlineData(Strictness.Error, ProblemSeverity.Critical)]
    [InlineData(Strictness.Warning, ProblemSeverity.Warning)]
    [InlineData(Strictness.Info, ProblemSeverity.Info)]
    public void Applier_maps_strictness_to_severity(Strictness s, ProblemSeverity expected)
    {
        var profile = new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["shared-db"] = s });

        var result = ProfileApplier.Apply([Problem("shared-db", ProblemSeverity.Critical)], profile);

        Assert.Equal(expected, result.Single().Severity);
    }

    // ── цикл сервисов: порог из профиля ──

    [Fact]
    public void Cycle_threshold_is_a_profile_parameter()
    {
        // ребро 0.8 образует цикл при пороге 0.8, но не при 0.9
        SystemEdge Http(string a, string b) => new(a, b, "http-call", EdgeVia.Config, 0.8);
        var graph = new SystemGraph([Svc("a"), Svc("b")], [Http("a", "b"), Http("b", "a")], []);

        Assert.Single(ServiceCycleDetector.Detect(graph));
        Assert.Single(ServiceCycleDetector.Detect(graph, 0.8));
        Assert.Empty(ServiceCycleDetector.Detect(graph, 0.9));
    }

    // ── GetSystemHandler: профиль применяется к ответу ──

    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_prof_" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Handler_without_profile_behaves_as_before()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("a")], [], [Problem("service-cycle", ProblemSeverity.Critical)]);

        var dto = new GetSystemHandler(new FakeSource(graph)).Handle(new GetSystemQuery(dir))!;

        Assert.Contains(dto.Problems!, p => p.Kind == "service-cycle");
    }

    [Fact]
    public void Handler_applies_profile_override_to_problems()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("a")], [], [Problem("shared-db", ProblemSeverity.Critical)]);
        var profile = new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["shared-db"] = Strictness.Off });

        var dto = new GetSystemHandler(new FakeSource(graph)).Handle(new GetSystemQuery(dir, Profile: profile))!;

        Assert.DoesNotContain(dto.Problems ?? [], p => p.Kind == "shared-db");
    }

    [Fact]
    public void Handler_recomputes_cycle_with_profile_threshold()
    {
        var dir = TempDir();
        SystemEdge Http(string a, string b) => new(a, b, "http-call", EdgeVia.Config, 0.8);
        // источник нашёл цикл при пороге 0.8; профиль требует 0.9 → цикла быть не должно
        var graph = new SystemGraph([Svc("a"), Svc("b")], [Http("a", "b"), Http("b", "a")],
            [Problem("service-cycle", ProblemSeverity.Critical), Problem("shared-db", ProblemSeverity.Critical)]);
        var strict = new ProjectProfile(Params: new ProfileParams(ServiceCycleMinConfidence: 0.9));

        var dto = new GetSystemHandler(new FakeSource(graph)).Handle(new GetSystemQuery(dir, Profile: strict))!;

        Assert.DoesNotContain(dto.Problems!, p => p.Kind == "service-cycle");
        Assert.Contains(dto.Problems!, p => p.Kind == "shared-db"); // прочее не тронуто
    }
}
