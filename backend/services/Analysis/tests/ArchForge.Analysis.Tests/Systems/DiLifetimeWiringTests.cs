using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Analysis.Infrastructure.Roslyn;

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
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
