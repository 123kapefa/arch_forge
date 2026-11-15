using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Правило double-validation: детектор над фактами IValidationSource, сопоставление поле ↔ VO, линза, профиль, каталог.
public class DoubleValidationTests
{
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_valid_det_" + Guid.NewGuid().ToString("N"))).FullName;

    private static ValidatorRuleSite R(string validator, string member, int line = 10, string layer = "Application", params string[] checks) =>
        new(validator, member, checks.Length == 0 ? ["MaximumLength"] : checks, "/p/V.cs", line, layer);
    private static ValueObjectSite Vo(string name, string layer = "Domain") => new(name, "/p/Vo.cs", 5, layer);

    private static ServiceAnalysis Sa(string name, string location, string? nodeId, ValidatorRuleSite[] rules, ValueObjectSite[] vos) =>
        new(new DiscoveredService(name, "/p/" + name, "docker-compose.yml"), location, Result: null)
        { NodeId = nodeId, Validation = new ValidationFacts(rules, vos) };

    [Fact]
    public void Field_matches_value_object_by_exact_suffix_or_reverse_name()
    {
        var p = Assert.Single(DoubleValidationDetector.Detect([Sa("tags", "tags", null,
            [R("V", "Inn", 1), R("V", "Title", 2), R("V", "CustomerInn", 3), R("V", "Comment", 4), R("V", "Id", 5)],
            [Vo("Inn"), Vo("TagTitle"), Vo("Id")])]));
        Assert.Equal(DoubleValidationDetector.Kind, p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Equal(ProblemCategory.Convention, p.Category);
        Assert.Equal(["V.Inn ↔ Inn", "V.Title ↔ TagTitle", "V.CustomerInn ↔ Inn", "V.Id ↔ Id"], p.Items!.Select(i => i.Name).ToArray());   // Comment — без VO
        Assert.Contains("MaximumLength", p.Items![0].Note);
    }

    [Fact]
    public void No_value_objects_or_no_match_or_tests_means_no_problem_and_monorepo_node_is_used()
    {
        Assert.Empty(DoubleValidationDetector.Detect([
            Sa("a", "a", null, [R("V", "Title")], []),
            Sa("b", "b", null, [R("V", "Comment")], [Vo("TagTitle")]),
            Sa("c", "c", null, [R("V", "Title", layer: "tests")], [Vo("Title")]),
            Sa("d", "d", null, [R("V", "Title")], [Vo("Title", layer: "tests")])]));
        var p = Assert.Single(DoubleValidationDetector.Detect([Sa("x", "x", "edu/x", [R("V", "Title"), R("V", "Title")], [Vo("Title")])]));
        Assert.Equal("edu/x", p.Location);
        Assert.Single(p.Items!);                                                   // повтор одного поля — одна запись
    }

    [Fact]
    public void Short_vo_names_do_not_match_by_reverse_suffix_and_items_are_capped()
    {
        Assert.Empty(DoubleValidationDetector.Detect([Sa("s", "s", null, [R("V", "UserId")], [Vo("Id")])]));    // Id слишком короткое для обратного совпадения
        var many = Enumerable.Range(0, DoubleValidationDetector.MaxItems + 20).Select(i => R("V" + i, "Title", i)).ToArray();
        var p = Assert.Single(DoubleValidationDetector.Detect([Sa("big", "big", null, many, [Vo("Title")])]));
        Assert.Equal(DoubleValidationDetector.MaxItems, p.Items!.Count);
    }

    [Fact]
    public void Catalog_has_the_rule_soft_convention_off_on_simple()
    {
        var rule = RuleCatalog.Find("double-validation")!;
        Assert.Equal(ProblemCategory.Convention, rule.Category);
        Assert.NotNull(rule.Example);
        Assert.Equal(Strictness.Off, rule.DefaultFor(ProjectLevel.Simple));
    }

    // ── сквозь обработчик системы ──

    private sealed class FakeSource(SystemGraph g) : ISystemSource
    {
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default) => g;
    }
    private sealed class FakeDiscovery : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => [new("orders", Path.Combine(root, "orders"), "docker-compose.yml")];
    }
    private sealed class FakeTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => [];
    }
    private sealed class FakeValidation : IValidationSource
    {
        public int Reads;
        public ValidationFacts Read(string root, CancellationToken ct = default) { Reads++; return new([R("V", "Title")], [Vo("TagTitle")]); }
    }
    private sealed class ThrowingValidation : IValidationSource
    {
        public ValidationFacts Read(string root, CancellationToken ct = default) => throw new IOException("boom");
    }

    [Fact]
    public void Handler_reports_it_only_with_drift_lens_profile_controls_it_and_reading_is_lazy()
    {
        var src = new FakeValidation();
        var h = new GetSystemHandler(
            new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
            new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), validation: src));
        var r = h.Handle(new GetSystemQuery(Root, Drift: true))!;
        var p = Assert.Single(r.Problems!, x => x.Kind == DoubleValidationDetector.Kind);
        Assert.Equal("convention", p.Category);
        Assert.Contains("Value Object", p.Fix);
        Assert.Equal(1, src.Reads);

        h.Handle(new GetSystemQuery(Root, Layers: true));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(ProjectLevel.Simple)));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["double-validation"] = Strictness.Off })));
        Assert.Equal(1, src.Reads);
    }

    [Fact]
    public void Failing_source_does_not_break_the_service()
    {
        var sa = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), validation: new ThrowingValidation()).Analyze(Root, ConventionPack.Default()));
        Assert.NotNull(sa.Result);
        Assert.Empty(sa.Validation.Rules);
    }

    [Fact]
    public void Several_matching_vos_are_all_named_and_short_field_names_match_only_exactly()
    {
        var p = Assert.Single(DoubleValidationDetector.Detect([Sa("s", "s", null, [R("V", "Customer.Name"), R("V", "No")], [Vo("UserName"), Vo("CustomerName"), Vo("OrderNo")])]));
        var item = Assert.Single(p.Items!);                                         // No — короткое, к OrderNo не липнет
        Assert.Equal("V.Customer.Name ↔ CustomerName / UserName", item.Name);
    }

    [Fact]
    public void Matching_does_not_scale_with_vo_count_times_rule_count()
    {
        var rules = Enumerable.Range(0, 5000).Select(i => R("V" + i, "Field" + i, i)).ToArray();
        var vos = Enumerable.Range(0, 5000).Select(i => Vo("Other" + i)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(DoubleValidationDetector.Detect([Sa("big", "big", null, rules, vos)]));
        Assert.True(sw.ElapsedMilliseconds < 500, $"слишком долго: {sw.ElapsedMilliseconds} мс");   // раньше ~1,2 с на 5000×5000
    }
}
