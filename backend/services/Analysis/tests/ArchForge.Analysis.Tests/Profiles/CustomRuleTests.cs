using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Tests.Profiles;

public class CustomRuleTests
{
    private static TypeInfo T(string name, string layer, string file, string role = "", params string[] bases) =>
        new(name, "class", layer, role, "heuristic", bases, file, [], []);

    private static AnalysisResult R(IReadOnlyList<TypeInfo> types, params TypeEdge[] edges) =>
        new(types, edges, new Coverage(0, 0, 0), []);

    private static ServiceAnalysis A(AnalysisResult? r) => new(new DiscoveredService("orders", "/x", "/x/compose.yml"), "sys.orders", r);

    private static CustomRuleDto Rule(string template, string strict = "Warning", string? suffix = null, string? baseType = null,
        string? from = null, string? to = null, string? folder = null, string? role = null) =>
        new("r1", template, "Моё правило", strict, suffix, baseType, from, to, folder, role);

    private static IReadOnlyList<SystemProblem> Run(CustomRuleDto rule, AnalysisResult r) => CustomRuleDetector.Detect([A(r)], [rule]);

    [Fact]
    public void Suffix_base_flags_types_without_the_base()
    {
        var r = R([T("CreateHandler", "Application", "a/CreateHandler.cs", "", "IHandler"), T("DeleteHandler", "Application", "a/DeleteHandler.cs"),
                   T("IHandler", "Application", "a/IHandler.cs")]);

        var p = Assert.Single(Run(Rule(CustomRuleTemplates.SuffixBase, suffix: "Handler", baseType: "IHandler"), r));

        Assert.Equal("custom:r1", p.Kind);
        Assert.Equal(ProblemSeverity.Warning, p.Severity);
        Assert.Equal(["DeleteHandler"], p.Items!.Select(i => i.Name));
    }

    [Fact]
    public void Suffix_base_accepts_generic_base_and_skips_tests_layer()
    {
        var r = R([T("OkHandler", "Application", "a.cs", "", "IHandler<Cmd>"), T("TestHandler", "tests", "t.cs")]);

        Assert.Empty(Run(Rule(CustomRuleTemplates.SuffixBase, suffix: "Handler", baseType: "IHandler"), r));
    }

    [Fact]
    public void Layer_no_ref_flags_edges_between_the_two_layers_once()
    {
        var r = R([T("Order", "Domain", "d/Order.cs"), T("Repo", "Infrastructure", "i/Repo.cs"), T("Svc", "Application", "a/Svc.cs")],
            new TypeEdge("Order", "Repo", "uses"), new TypeEdge("Order", "Repo", "holds"), new TypeEdge("Svc", "Repo", "uses"));

        var p = Assert.Single(Run(Rule(CustomRuleTemplates.LayerNoRef, strict: "Error", from: "domain", to: "INFRASTRUCTURE"), r));

        Assert.Equal(ProblemSeverity.Critical, p.Severity);
        Assert.Equal(["Order → Repo"], p.Items!.Select(i => i.Name));
    }

    [Fact]
    public void Forbid_in_folder_combines_suffix_and_folder()
    {
        var r = R([T("OrderDto", "Application", "src/Domain/Models/OrderDto.cs"), T("Order", "Domain", "src/Domain/Models/Order.cs"),
                   T("UserDto", "Application", "src/Contracts/UserDto.cs")]);

        var p = Assert.Single(Run(Rule(CustomRuleTemplates.ForbidInFolder, suffix: "Dto", folder: "Domain"), r));

        Assert.Equal(["OrderDto"], p.Items!.Select(i => i.Name));
    }

    [Fact]
    public void Role_in_folder_flags_role_outside_folder()
    {
        var r = R([T("CreateOrder", "Application", "src/Features/CreateOrder.cs", "Command"), T("Misplaced", "Application", "src/Other/Misplaced.cs", "command")]);

        var p = Assert.Single(Run(Rule(CustomRuleTemplates.RoleInFolder, role: "Command", folder: "Features"), r));

        Assert.Equal(["Misplaced"], p.Items!.Select(i => i.Name));
    }

    [Theory]
    [InlineData("src/Domain/X.cs", "Domain", true)]
    [InlineData("src\\Domain\\X.cs", "domain", true)]
    [InlineData("src/Domain/Models/X.cs", "Domain/Models", true)]
    [InlineData("src/MyDomain/X.cs", "Domain", false)]     // сегмент целиком, не подстрока
    [InlineData("src/X/Domain.cs", "Domain", false)]       // имя файла — не папка
    public void Folder_matches_whole_segments(string file, string folder, bool expected) =>
        Assert.Equal(expected, CustomRuleDetector.InFolder(file, folder));

    [Fact]
    public void Off_rule_and_failed_service_produce_nothing()
    {
        var r = R([T("DeleteHandler", "Application", "a.cs")]);

        Assert.Empty(Run(Rule(CustomRuleTemplates.SuffixBase, strict: "Off", suffix: "Handler", baseType: "IHandler"), r));
        Assert.Empty(CustomRuleDetector.Detect([A(null)], [Rule(CustomRuleTemplates.SuffixBase, suffix: "Handler", baseType: "IHandler")]));
        Assert.Empty(CustomRuleDetector.Detect([A(r)], null));
    }

    [Fact]
    public void Custom_problem_keeps_its_own_severity_through_the_profile()
    {
        var p = new SystemProblem(ProblemSeverity.Warning, "custom:r1", "t", "loc");

        var applied = ProfileApplier.Apply([p], new ProjectProfile(ProjectLevel.Simple));

        Assert.Equal(p, Assert.Single(applied));
    }

    [Fact]
    public void Profile_with_custom_rules_maps_and_roundtrips()
    {
        var dto = new ProfileDto("Standard", CustomRules: [Rule(CustomRuleTemplates.SuffixBase, suffix: "Handler", baseType: "IHandler")]);

        Assert.True(ProfileMapping.TryToProfile(dto, out var profile, out _));
        Assert.Equal("r1", Assert.Single(profile.CustomRules!).Id);
    }

    // --- валидация ---

    private static IReadOnlyList<string> Validate(params CustomRuleDto[] rules) =>
        ProfileValidator.Validate(new ProfileDto("Standard", CustomRules: rules), ProfileMapping.KnownRuleIds);

    [Fact]
    public void Valid_rules_of_every_template_pass()
    {
        Assert.Empty(Validate(
            Rule(CustomRuleTemplates.SuffixBase, suffix: "Handler", baseType: "IHandler") with { Id = "a" },
            Rule(CustomRuleTemplates.LayerNoRef, from: "Domain", to: "Infrastructure") with { Id = "b" },
            Rule(CustomRuleTemplates.ForbidInFolder, baseType: "IDto", folder: "Domain") with { Id = "c" },
            Rule(CustomRuleTemplates.RoleInFolder, role: "Command", folder: "Features") with { Id = "d" }));
    }

    [Fact]
    public void Missing_fields_unknown_template_bad_id_and_duplicates_are_reported()
    {
        var errors = Validate(
            Rule(CustomRuleTemplates.SuffixBase, suffix: "Handler"),                        // нет BaseType
            Rule(CustomRuleTemplates.LayerNoRef, from: "Domain", to: "domain") with { Id = "b" },
            Rule("magic") with { Id = "c" },
            Rule(CustomRuleTemplates.RoleInFolder, role: "X", folder: "Y") with { Id = "Bad Id" },
            Rule(CustomRuleTemplates.RoleInFolder, strict: "Loud", role: "X", folder: "Y") with { Id = "r1" });

        Assert.Contains(errors, e => e.Contains("BaseType"));
        Assert.Contains(errors, e => e.Contains("слои должны различаться"));
        Assert.Contains(errors, e => e.Contains("magic"));
        Assert.Contains(errors, e => e.Contains("идентификатор"));
        Assert.Contains(errors, e => e.Contains("Loud"));
        Assert.Contains(errors, e => e.Contains("повторяется"));
    }

    [Fact]
    public void Count_and_field_length_are_limited()
    {
        var many = Enumerable.Range(0, ProfileValidator.MaxCustomRules + 1)
            .Select(i => Rule(CustomRuleTemplates.SuffixBase, suffix: "H", baseType: "I") with { Id = $"r{i}" }).ToArray();
        Assert.Contains(Validate(many), e => e.Contains("не больше"));

        var longField = Rule(CustomRuleTemplates.SuffixBase, suffix: new string('x', 200), baseType: "I");
        Assert.Contains(Validate(longField), e => e.Contains("Suffix"));
    }

    [Fact]
    public void Null_rule_entry_does_not_throw()
    {
        var dto = new ProfileDto("Standard", CustomRules: [null!]);

        Assert.NotEmpty(ProfileValidator.Validate(dto, ProfileMapping.KnownRuleIds));
    }
}
