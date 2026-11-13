using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Tests.Systems;

// Монорепо с ОДНИМ корневым compose: сервисы находятся по .sln (своего compose у них нет) и привязываются к узлам графа по имени.
// Раньше такой код-разбор был пуст: нет слоёв, дрейфа, вызовов (память проекта: analyzer-needs-per-service-compose).
public class MonorepoAnalysisTests
{
    // корень должен существовать: ServiceAnalyzer не работает по несуществующей папке (сами сервисы подставлены фейковым discovery)
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_mono_" + Guid.NewGuid().ToString("N"))).FullName;

    private static DiscoveredService Sln(string name, string? compose = null) =>
        new(name, Path.Combine(Root, "backend", name), compose);

    private static SystemNode Svc(string name) => new($"edu/{name}", name, SystemNodeKind.Service);

    private static readonly IReadOnlyList<SystemNode> Nodes =
    [
        Svc("comment-service"), Svc("comment-service-migrations"),
        Svc("education-service"), Svc("auth-service"), Svc("tag-service"),
        new("edu/postgres", "postgres", SystemNodeKind.Datastore),
    ];

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class FakeTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) =>
            [new("RegisterHandler", "class", [], "(Dep d)", "Application", Path.Combine(path, "x.cs"))];
    }

    private static ServiceAnalyzer Analyzer(params DiscoveredService[] found) =>
        new(new FakeDiscovery(found), new FakeTypes(), new TypeClassifier());

    // ── сопоставление .sln ↔ узел ──

    [Fact]
    public void Matches_by_exact_normalized_name_and_by_stem_and_skips_satellites_and_umbrella()
    {
        var m = ServiceNodeMatcher.Match(
            [Sln("CommentService"), Sln("EducationContentService"), Sln("backend"), Sln("Shared")], Nodes, ServiceNaming.Default);

        Assert.Equal(2, m.Count);
        Assert.Equal("edu/comment-service", m.Single(x => x.Svc.Name == "CommentService").Node.Id);       // не comment-service-migrations
        Assert.Equal("edu/education-service", m.Single(x => x.Svc.Name == "EducationContentService").Node.Id);   // стем «education»
        Assert.DoesNotContain(m, x => x.Svc.Name is "backend" or "Shared");
    }

    [Fact]
    public void Stem_must_be_a_prefix_not_just_a_substring()
    {
        // «OAuthGateway» содержит «auth», но не начинается с него — это не auth-service
        Assert.Empty(ServiceNodeMatcher.Match([Sln("OAuthGateway")], Nodes, ServiceNaming.Default));
    }

    [Fact]
    public void Two_solutions_for_one_node_prefer_exact_and_otherwise_are_dropped()
    {
        var exactWins = ServiceNodeMatcher.Match([Sln("TagService"), Sln("TagServiceLegacy")], Nodes, ServiceNaming.Default);
        Assert.Equal("TagService", Assert.Single(exactWins).Svc.Name);

        var bothStem = ServiceNodeMatcher.Match([Sln("EducationContentService"), Sln("EducationImportService")], Nodes, ServiceNaming.Default);
        Assert.Empty(bothStem);   // два претендента без точного совпадения — не гадаем
    }

    // ── ServiceAnalyzer ──

    [Fact]
    public void Monorepo_services_are_analyzed_and_bound_to_their_nodes()
    {
        var an = Analyzer(Sln("CommentService"), Sln("EducationContentService"), Sln("backend"))
            .Analyze(Root, ConventionPack.Default(), nodes: Nodes);

        Assert.Equal(2, an.Count);
        var c = an.Single(a => a.Service.Name == "CommentService");
        Assert.Equal("edu/comment-service", c.NodeId);
        Assert.Equal("edu", c.Location);                       // система узла (общий namespace-префикс id)
        Assert.All(an, a => Assert.NotNull(a.Result));
    }

    [Fact]
    public void Without_nodes_or_without_matches_monorepo_stays_empty_as_before()
    {
        Assert.Empty(Analyzer(Sln("CommentService")).Analyze(Root, ConventionPack.Default()));
        Assert.Empty(Analyzer(Sln("Nothing")).Analyze(Root, ConventionPack.Default(), nodes: Nodes));
    }

    [Fact]
    public void When_any_service_has_own_compose_the_old_rules_apply()
    {
        var an = Analyzer(Sln("CommentService", compose: "docker-compose.yml"), Sln("EducationContentService"))
            .Analyze(Root, ConventionPack.Default(), nodes: Nodes);

        var only = Assert.Single(an);                           // как раньше: только сервис со своим compose
        Assert.Equal("CommentService", only.Service.Name);
        Assert.Null(only.NodeId);
    }

    // ── потребители привязки ──

    [Fact]
    public void Problems_of_monorepo_service_point_at_its_node_not_the_whole_system()
    {
        var an = Analyzer(Sln("CommentService")).Analyze(Root, ConventionPack.Default(), nodes: Nodes);
        var drift = DriftDetector.Detect(an);
        Assert.All(drift, p => Assert.Equal("edu/comment-service", p.Location));
        Assert.NotEmpty(drift);
    }

    [Fact]
    public void Call_graph_uses_bound_node_as_source_even_when_names_do_not_match()
    {
        var graph = new SystemGraph(Nodes, [], []);
        var sa = new ServiceAnalysis(Sln("EducationContentService"), "edu", Result: null)
        {
            NodeId = "edu/education-service",
            HttpCalls = [new HttpCallHit("GET", "http://auth-service/api/me", "/x/Client.cs", 5, "GET http://auth-service/api/me")],
        };
        var g = HttpCallGraphBuilder.Enrich(graph, [sa]);
        var edge = Assert.Single(g.Edges, e => e.Kind == "http-call");
        Assert.Equal("edu/education-service", edge.Source);
        Assert.Equal("edu/auth-service", edge.Target);
    }

    [Fact]
    public void Layer_summary_goes_to_bound_node_even_without_name_signal()
    {
        var types = new List<TypeInfo>
        {
            new("Item", "class", "Domain", "Entity", "marker", [], "/r/backend/EducationContentService/src/Domain/Item.cs", [], []),
        };
        var result = new AnalysisResult(types, [], new Coverage(1, 0, 0), []);
        var sa = new ServiceAnalysis(Sln("EducationContentService"), "edu", result) { NodeId = "edu/education-service" };

        var entries = MicroserviceLayerBuilder.Build([sa], Nodes.Where(n => n.Kind == SystemNodeKind.Service).Select(n => (n.Id, n.Name)).ToList());
        Assert.Contains(MicroserviceLayerBuilder.NodeKey("edu/education-service", "education-service"), entries.Keys);
    }

    // ── замечания ревью ──

    [Theory]
    [InlineData("AuthorizationService")]    // «auth» — не целое слово в начале имени
    [InlineData("AuthorsImport")]
    [InlineData("OrderingService")]
    public void Stem_must_be_a_whole_leading_word(string sln)
    {
        var nodes = new List<SystemNode> { Svc("auth-service"), Svc("order-service") };
        Assert.Empty(ServiceNodeMatcher.Match([Sln(sln)], nodes, ServiceNaming.Default));
    }

    [Theory]
    [InlineData("EducationContentService", "edu/education-service")]
    [InlineData("Education.Content.Service", "edu/education-service")]
    [InlineData("EducationService", "edu/education-service")]            // точное
    [InlineData("TelegramBotNotifier", "edu/telegram-bot-service")]       // стем из двух слов
    public void Whole_word_stems_are_matched(string sln, string nodeId)
    {
        var nodes = new List<SystemNode> { Svc("education-service"), Svc("telegram-bot-service") };
        Assert.Equal(nodeId, Assert.Single(ServiceNodeMatcher.Match([Sln(sln)], nodes, ServiceNaming.Default)).Node.Id);
    }

    [Fact]
    public void Satellite_solutions_are_not_services()
    {
        Assert.Empty(ServiceNodeMatcher.Match([Sln("CommentServiceMigrator"), Sln("CommentServiceSeeder")], Nodes, ServiceNaming.Default));
    }

    [Fact]
    public void Layer_summary_of_bound_node_ignores_services_folder_above_the_service()
    {
        // монорепо с раскладкой repo/backend/Services/CommentService/…: «/Services/» в абсолютном пути не должно перебить привязку
        var file = "/r/backend/Services/CommentService/src/CommentService.Domain/Item.cs";
        var types = new List<TypeInfo> { new("Item", "class", "Domain", "Entity", "marker", [], file, [], []) };
        var sa = new ServiceAnalysis(new DiscoveredService("CommentService", "/r/backend/Services/CommentService", null), "edu",
            new AnalysisResult(types, [], new Coverage(1, 0, 0), [])) { NodeId = "edu/comment-service" };

        var entries = MicroserviceLayerBuilder.Build([sa], Nodes.Where(n => n.Kind == SystemNodeKind.Service).Select(n => (n.Id, n.Name)).ToList());
        var e = Assert.Single(entries);
        Assert.Equal(MicroserviceLayerBuilder.NodeKey("edu/comment-service", "comment-service"), e.Key);   // не «services::…» по папке выше
        Assert.Equal("/r/backend/Services/CommentService", e.Value.Root);                                  // корень drill — папка сервиса
    }
}
