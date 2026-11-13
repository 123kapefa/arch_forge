using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests.Systems;

// Вызовы из клиентских библиотек (*.Contracts) приписываются сервису-ПОТРЕБИТЕЛЮ, который подключает и регистрирует библиотеку.
public class ClientLibraryCallsTests
{
    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class FakeTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => [];
    }

    private sealed class FakeHttp(Dictionary<string, IReadOnlyList<HttpCallHit>> byRoot) : IHttpCallSource
    {
        public List<string> Reads { get; } = [];
        public IReadOnlyList<HttpCallHit> Read(string root, CancellationToken ct = default)
        {
            Reads.Add(root);
            return byRoot.TryGetValue(root, out var h) ? h : [];
        }
    }

    private sealed class FakeLibs(Func<string, IReadOnlyList<ClientLibrary>> f) : IClientLibraryResolver
    {
        public IReadOnlyList<ClientLibrary> Resolve(string consumerRoot, string systemRoot, CancellationToken ct = default) => f(consumerRoot);
    }

    private static readonly string Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_libs_" + Guid.NewGuid().ToString("N"))).FullName;
    private static readonly DiscoveredService Consumer = new("Comment", Path.Combine(Root, "comment"), "docker-compose.yml");
    private static readonly string LibDir = Path.Combine(Root, "tag", "src", "Tag.Contracts");

    private static HttpCallHit Hit(string route) => new("GET", route, Path.Combine(LibDir, "Client.cs"), 7, $"GET {route}", ViaBase: true, ConfigKey: "TagServiceOptions:Url");

    private static ServiceAnalyzer Analyzer(FakeHttp http, IClientLibraryResolver? libs) =>
        new(new FakeDiscovery([Consumer]), new FakeTypes(), new TypeClassifier(), httpCalls: http, libraries: libs);

    [Fact]
    public void Library_calls_are_added_to_the_consumer_with_library_marker()
    {
        var http = new FakeHttp(new() { [LibDir] = [Hit("/api/tags")] });
        var an = Analyzer(http, new FakeLibs(_ => [new ClientLibrary("Tag.Contracts", LibDir)])).Analyze(Root, ConventionPack.Default());

        var call = Assert.Single(Assert.Single(an).HttpCalls);
        Assert.Equal("/api/tags", call.Url);
        Assert.Equal("TagServiceOptions:Url", call.ConfigKey);
        Assert.Contains("[библиотека Tag.Contracts]", call.Evidence);
    }

    [Fact]
    public void No_libraries_or_no_resolver_means_only_own_calls()
    {
        var http = new FakeHttp(new() { [LibDir] = [Hit("/api/tags")] });
        Assert.Empty(Assert.Single(Analyzer(http, new FakeLibs(_ => [])).Analyze(Root, ConventionPack.Default())).HttpCalls);
        Assert.Empty(Assert.Single(Analyzer(http, null).Analyze(Root, ConventionPack.Default())).HttpCalls);
    }

    [Fact]
    public void Failing_resolver_does_not_break_the_service()
    {
        var http = new FakeHttp(new() { [Consumer.Root] = [new HttpCallHit("GET", "http://x/a", "/f.cs", 1, "GET http://x/a")] });
        var sa = Assert.Single(Analyzer(http, new FakeLibs(_ => throw new IOException("boom"))).Analyze(Root, ConventionPack.Default()));
        Assert.NotNull(sa.Result);
        Assert.Single(sa.HttpCalls);                       // собственные вызовы сохранились
    }

    [Fact]
    public void Library_hits_resolve_against_consumer_config_and_target_is_the_owner_service()
    {
        // сквозь сборщик графа: ключ из библиотеки резолвится по конфигурации ПОТРЕБИТЕЛЯ
        var graph = new SystemGraph(
            [new SystemNode("edu/comment-service", "comment-service", SystemNodeKind.Service), new SystemNode("edu/tag-service", "tag-service", SystemNodeKind.Service)],
            [], [], ConfigUrls: [new ConfigUrl("edu/comment-service", "tagserviceoptions:url", "http://tag-service:8003/", FromEnvironment: true)]);
        var sa = new ServiceAnalysis(Consumer, "edu", Result: null) { NodeId = "edu/comment-service", HttpCalls = [Hit("/api/tags")] };
        var g = HttpCallGraphBuilder.Enrich(graph, [sa]);
        var edge = Assert.Single(g.Edges, e => e.Kind == "http-call");
        Assert.Equal("edu/comment-service", edge.Source);
        Assert.Equal("edu/tag-service", edge.Target);
        Assert.Equal("/api/tags", Assert.Single(edge.Calls!).Route);
    }
}
