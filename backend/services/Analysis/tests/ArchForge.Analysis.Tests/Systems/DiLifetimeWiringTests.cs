using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Analysis.Infrastructure.Manifests;
using ArchForge.Analysis.Infrastructure.Roslyn;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Tests.Systems;

// E4.2, боевой путь: GetSystem → ServiceAnalyzer читает IDiSource (настоящий Roslyn) → ServiceAnalysis.Di → DiLifetimeDetector → проблемы ответа.
public class DiLifetimeWiringTests
{
    private const string Program = """
        public class AppDbContext { }
        public interface IPaymentService { }
        public class PaymentService : IPaymentService { public PaymentService(AppDbContext db) { } }
        public static class Startup
        {
            public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection s)
            {
                s.AddDbContext<AppDbContext>();
                s.AddSingleton<IPaymentService, PaymentService>();
            }
        }
        """;

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
    private sealed class ThrowingDi : IDiSource
    {
        public DiModel Read(string root, CancellationToken ct = default) => throw new IOException("boom");
    }

    private static GetSystemHandler Handler(IDiSource di) => new(
        new FakeSource(new SystemGraph([new SystemNode("orders/orders-api", "orders-api", SystemNodeKind.Service)], [], [])),
        new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), di: di));

    private static string Target()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "af_diwire_" + Guid.NewGuid().ToString("N"))).FullName;
        var svc = Directory.CreateDirectory(Path.Combine(root, "orders")).FullName;
        File.WriteAllText(Path.Combine(svc, "Program.cs"), Program);
        return root;
    }

    [Fact]
    public void Layers_lens_reports_scoped_captured_by_singleton_through_real_roslyn_source()
    {
        var root = Target();
        try
        {
            var r = Handler(new RoslynDiSource()).Handle(new GetSystemQuery(root, Layers: true))!;
            var p = Assert.Single(r.Problems!, x => x.Kind == "di-captive");
            Assert.Contains("Singleton держит Scoped", p.Title);
            Assert.Contains(p.Items!, i => i.Name == "PaymentService → AppDbContext");

            // без линзы слоёв DI не читается и проблемы нет
            var off = Handler(new RoslynDiSource()).Handle(new GetSystemQuery(root, Drift: true))!;
            Assert.DoesNotContain(off.Problems ?? [], x => x.Kind.StartsWith("di-"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Failing_di_source_does_not_break_the_service()
    {
        var root = Target();
        try
        {
            var sa = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), di: new ThrowingDi())
                .Analyze(root, ConventionPack.Default(), readDi: true));
            Assert.NotNull(sa.Result);
            Assert.Null(sa.Di);
            Assert.True(sa.DiReadFailed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CancellingDi : IDiSource
    {
        public DiModel Read(string root, CancellationToken ct = default) => throw new OperationCanceledException();
    }

    [Fact]
    public void Cancellation_is_not_swallowed_and_missing_source_is_marked_as_failed()
    {
        var root = Target();
        try
        {
            // Parallel.For оборачивает отмену из тела в AggregateException — важно, что она не проглочена
            var ex = Assert.Throws<AggregateException>(() =>
                new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier(), di: new CancellingDi()).Analyze(root, ConventionPack.Default(), readDi: true));
            Assert.All(ex.InnerExceptions, e => Assert.IsAssignableFrom<OperationCanceledException>(e));
            var noSource = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier()).Analyze(root, ConventionPack.Default(), readDi: true));
            Assert.True(noSource.DiReadFailed);
            var notAsked = Assert.Single(new ServiceAnalyzer(new FakeDiscovery(), new FakeTypes(), new TypeClassifier()).Analyze(root, ConventionPack.Default()));
            Assert.False(notAsked.DiReadFailed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Failed_di_read_is_a_degradation_in_the_response()
    {
        var root = Target();
        try
        {
            var r = Handler(new ThrowingDi()).Handle(new GetSystemQuery(root, Layers: true))!;
            Assert.Contains(r.Degradations ?? [], d => d.Contains("DI сервиса «orders» не прочитан"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Container_built_analyzer_gets_the_roslyn_di_source()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddManifests();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<CachingDiSource>(sp.GetRequiredService<IDiSource>());

        var root = Target();
        try
        {
            File.WriteAllText(Path.Combine(root, "orders", "Orders.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, "orders", "docker-compose.yml"), "services:\n  orders-api:\n    image: x\n");
            var sa = Assert.Single(sp.GetRequiredService<ServiceAnalyzer>().Analyze(root, ConventionPack.Default(), readDi: true));
            Assert.False(sa.DiReadFailed);
            Assert.NotNull(sa.Di);
            Assert.NotEmpty(sa.Di!.Registrations);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryAdd_scoped_over_singleton_free_graph_is_not_flagged()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "af_diwire_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var svc = Directory.CreateDirectory(Path.Combine(root, "orders")).FullName;
            File.WriteAllText(Path.Combine(svc, "Program.cs"), """
                public class AppDbContext { }
                public interface IPaymentService { }
                public class PaymentService : IPaymentService { public PaymentService(AppDbContext db) { } }
                public static class Startup
                {
                    public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection s)
                    {
                        s.AddDbContext<AppDbContext>();
                        s.AddScoped<IPaymentService, PaymentService>();
                    }
                }
                """);
            var r = Handler(new RoslynDiSource()).Handle(new GetSystemQuery(root, Layers: true))!;
            Assert.DoesNotContain(r.Problems ?? [], x => x.Kind is "di-captive" or "di-missing");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
