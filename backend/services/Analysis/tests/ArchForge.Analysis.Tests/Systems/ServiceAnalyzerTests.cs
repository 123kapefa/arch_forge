using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class ServiceAnalyzerTests
{
    private sealed class StubDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class StubTypes(Func<string, IReadOnlyList<RawType>> read) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => read(path);
    }

    private static readonly RawType[] Some = [new("Foo", "class", [], "()", "Application", "x.cs")];

    // сервис С compose (иначе анализатор его отфильтрует — нет узлов в графе)
    private static DiscoveredService Svc(string root, string name) =>
        new(name, Path.Combine(root, name), "docker-compose.yml");

    private static ServiceAnalyzer Analyzer(IReadOnlyList<DiscoveredService> svcs, Func<string, IReadOnlyList<RawType>> read) =>
        new(new StubDiscovery(svcs), new StubTypes(read), new TypeClassifier());

    private static string TempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_sa_" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Analyzes_each_compose_service_with_namespace_location()
    {
        var root = TempRoot();
        try
        {
            var res = Analyzer([Svc(root, "a"), Svc(root, "b")], _ => Some).Analyze(root, ConventionPack.Default());
            Assert.Equal(new[] { "a", "b" }, res.Select(r => r.Location)); // порядок discovery, location = Rel(root, svc.Root)
            Assert.All(res, r => Assert.NotNull(r.Result));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Service_without_compose_is_skipped()
    {
        var root = TempRoot();
        try
        {
            var svcs = new[] { new DiscoveredService("Lib", Path.Combine(root, "lib"), null) };
            Assert.Empty(Analyzer(svcs, _ => Some).Analyze(root, ConventionPack.Default())); // «голый» .sln → нет узлов → пропуск
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Broken_service_yields_null_result_not_throw()
    {
        var root = TempRoot();
        try
        {
            var one = Assert.Single(Analyzer([Svc(root, "bad")], _ => throw new IOException("boom")).Analyze(root, ConventionPack.Default()));
            Assert.Null(one.Result); // изоляция ошибок: сбой → null (линза поднимет Coverage), а не исключение
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Cancellation_is_observed()
    {
        var root = TempRoot();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => Analyzer([Svc(root, "a")], _ => Some).Analyze(root, ConventionPack.Default(), cts.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Missing_root_yields_empty() =>
        Assert.Empty(Analyzer([Svc("/x", "a")], _ => Some).Analyze("/no/such/root/archforge-xyz", ConventionPack.Default()));
}
