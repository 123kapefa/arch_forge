using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Одно решение + один compose с несколькими сервисами (product_site): дрейф должен привязываться к сервису, а не ко всей солюшн (#60).
public class ProjectFolderSplitTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_split_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* временная папка */ } }

    private string Backend => Path.Combine(_root, "backend");

    private void Proj(string rel)
    {
        var dir = Path.Combine(Backend, rel);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Path.GetFileName(rel) + ".csproj"), "<Project />");
    }

    private void Layout()
    {
        foreach (var p in new[] { "src/Services/Catalog/Catalog.API", "src/Services/Catalog/Catalog.Domain", "src/Services/User/User.API",
                                  "src/ApiGateway/Kalmykov.ApiGateway", "src/Shared/Kalmykov.Shared.Contracts" })
            Proj(p);
    }

    private static SystemNode Svc(string name) => new($"backend/{name}", name, SystemNodeKind.Service);

    private static readonly IReadOnlyList<SystemNode> Nodes =
    [
        Svc("api-gateway"), Svc("catalog-api"), Svc("user-api"), Svc("catalog-migrations"),
        new("backend/postgres", "postgres", SystemNodeKind.Datastore),
    ];

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    // тип с дрейфом: Handler без маркера в Application; File показывает, из какой папки он пришёл
    private sealed class FakeTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) =>
            [new("RegisterHandler", "class", [], "(Dep d)", "Application", Path.Combine(path, "x.cs"))];
    }

    private ServiceAnalyzer Analyzer(params DiscoveredService[] found) =>
        new(new FakeDiscovery(found), new FakeTypes(), new TypeClassifier());

    private DiscoveredService Sln() => new("KalmykovGroup.Server", Backend, Path.Combine(Backend, "docker-compose-dev.yaml"));

    [Fact]
    public void Solution_with_multi_service_compose_is_split_by_project_folders()
    {
        Layout();
        var an = Analyzer(Sln()).Analyze(_root, ConventionPack.Default(), nodes: Nodes);

        Assert.Equal(3, an.Count);   // gateway, catalog, user; Shared и мигратор — не сервисы
        Assert.Equal("backend/catalog-api", an.Single(a => a.Service.Name == "Catalog").NodeId);
        Assert.Equal(Path.Combine(Backend, "src/Services/Catalog"), an.Single(a => a.Service.Name == "Catalog").Service.Root);   // Catalog, не Catalog.API
        Assert.Equal("backend/api-gateway", an.Single(a => a.Service.Name == "ApiGateway").NodeId);
        Assert.All(an, a => Assert.NotNull(a.Result));
    }

    [Fact]
    public void Drift_goes_to_each_service_node_not_to_the_whole_solution()
    {
        Layout();
        var drift = DriftDetector.Detect(Analyzer(Sln()).Analyze(_root, ConventionPack.Default(), nodes: Nodes));

        Assert.NotEmpty(drift);
        Assert.All(drift, p => Assert.Contains(p.Location, new[] { "backend/catalog-api", "backend/user-api", "backend/api-gateway" }));
        Assert.Equal(drift.Count, drift.Select(p => p.Location).Distinct().Count());   // по находке на сервис
    }

    [Fact]
    public void Single_match_is_not_enough_solution_stays_whole()
    {
        Proj("src/Services/Catalog/Catalog.API");
        var an = Analyzer(Sln()).Analyze(_root, ConventionPack.Default(), nodes: Nodes);

        var only = Assert.Single(an);
        Assert.Equal("KalmykovGroup.Server", only.Service.Name);
        Assert.Null(only.NodeId);
    }

    [Fact]
    public void Without_nodes_or_with_one_service_in_compose_solution_stays_whole()
    {
        Layout();
        Assert.Equal("KalmykovGroup.Server", Assert.Single(Analyzer(Sln()).Analyze(_root, ConventionPack.Default())).Service.Name);
        var oneNode = new[] { Svc("catalog-api") };
        Assert.Equal("KalmykovGroup.Server", Assert.Single(Analyzer(Sln()).Analyze(_root, ConventionPack.Default(), nodes: oneNode)).Service.Name);
    }

    [Fact]
    public void Nested_separate_solution_is_not_taken_as_a_folder_of_the_umbrella()
    {
        Layout();
        var nested = new DiscoveredService("Catalog", Path.Combine(Backend, "src/Services/Catalog"), null);
        var parts = ProjectFolderSplitter.Split(_root, Sln(), Nodes, ArchForge.Analysis.Core.Profiles.ServiceNaming.Default,
            new HashSet<string> { nested.Root });
        Assert.DoesNotContain(parts, p => p.Svc.Root == nested.Root);
    }
}
