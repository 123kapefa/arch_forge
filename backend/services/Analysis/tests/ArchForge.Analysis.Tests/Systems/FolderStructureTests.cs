using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Правило folder-structure: детектор над фактами IFolderSource, привязка к узлу, линза, профиль, каталог.
public class FolderStructureTests
{
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_fold_det_" + Guid.NewGuid().ToString("N"))).FullName;

    private static FolderSite F(string name, string layer = "Application") => new(name, "/p/" + name + ".cs", layer);

    private static ServiceAnalysis Sa(string name, string location, string? nodeId, FolderSite[] folders, FolderSite[] names) =>
        new(new DiscoveredService(name, "/p/" + name, "docker-compose.yml"), location, Result: null)
        { NodeId = nodeId, Folders = new FolderFacts(folders, names) };

    [Fact]
    public void Folders_and_names_become_one_info_problem()
    {
        var p = Assert.Single(FolderStructureDetector.Detect([Sa("clients", "clients", null, [F("Core/Services")], [F("FounderResolver")])]));
        Assert.Equal(FolderStructureDetector.Kind, p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Equal(ProblemCategory.Convention, p.Category);
        Assert.Contains("1 папок", p.Title);
        Assert.Contains("1 файлов", p.Title);
        Assert.Equal(["Core/Services/", "FounderResolver"], p.Items!.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void Nothing_or_only_tests_is_no_problem_monorepo_node_is_used_and_items_are_capped()
    {
        Assert.Empty(FolderStructureDetector.Detect([Sa("a", "a", null, [], []), Sa("b", "b", null, [F("Helpers", "tests")], [F("XHelper", "tests")])]));
        Assert.Equal("edu/x", Assert.Single(FolderStructureDetector.Detect([Sa("x", "x", "edu/x", [F("Common")], [])])).Location);
        var many = Enumerable.Range(0, FolderStructureDetector.MaxItems + 20).Select(i => F("M" + i + "Manager")).ToArray();
        var p = Assert.Single(FolderStructureDetector.Detect([Sa("big", "big", null, [], many)]));
        Assert.Equal(FolderStructureDetector.MaxItems, p.Items!.Count);
        Assert.Contains("показано", p.Title);
    }

    [Fact]
    public void Catalog_has_the_rule_soft_convention_off_on_simple()
    {
        var rule = RuleCatalog.Find("folder-structure")!;
        Assert.Equal(ProblemCategory.Convention, rule.Category);
        Assert.NotNull(rule.Example);
        Assert.Equal(Strictness.Off, rule.DefaultFor(ProjectLevel.Simple));
    }

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
    private sealed class FakeFolders : IFolderSource
    {
        public int Reads;
        public FolderFacts Read(string root, CancellationToken ct = default) { Reads++; return new([F("Core/Services")], []); }
    }
    private sealed class ThrowingFolders : IFolderSource
    {
        public FolderFacts Read(string root, CancellationToken ct = default) => throw new IOException("boom");
    }

    [Fact]
    public void Handler_reports_it_only_with_drift_lens_profile_controls_it_and_reading_is_lazy()
    {
        var src = new FakeFolders();
        var h = new GetSystemHandler(
            new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
            new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), folders: src));
        var r = h.Handle(new GetSystemQuery(Root, Drift: true))!;
        var p = Assert.Single(r.Problems!, x => x.Kind == FolderStructureDetector.Kind);
        Assert.Equal("convention", p.Category);
        Assert.Contains("Features", p.Fix);
        Assert.Equal(1, src.Reads);

        h.Handle(new GetSystemQuery(Root, Layers: true));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(ProjectLevel.Simple)));
        h.Handle(new GetSystemQuery(Root, Drift: true, Profile: new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["folder-structure"] = Strictness.Off })));
        Assert.Equal(1, src.Reads);
    }

    [Fact]
    public void Failing_source_does_not_break_the_service()
    {
        var sa = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), folders: new ThrowingFolders()).Analyze(Root, ConventionPack.Default()));
        Assert.NotNull(sa.Result);
        Assert.Empty(sa.Folders.TechFolders);
    }
}
