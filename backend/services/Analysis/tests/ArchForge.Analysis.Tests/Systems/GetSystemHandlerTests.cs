using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

public class GetSystemHandlerTests
{
    private sealed class FakeSource(SystemGraph graph) : ISystemSource
    {
        public SystemGraph Read(string root, CancellationToken ct = default) => graph;
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class FakeTypes(IReadOnlyList<RawType> raw) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => raw;
    }

    [Fact]
    public void Null_for_missing_or_empty_path()
    {
        var h = new GetSystemHandler(new FakeSource(new SystemGraph([], [], [])));
        Assert.Null(h.Handle(new GetSystemQuery("/no/such/path/xyz-archforge")));
        Assert.Null(h.Handle(new GetSystemQuery("")));
    }

    [Fact]
    public void Maps_system_graph_for_existing_dir()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "archforge_sysh_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var g = new SystemGraph([new SystemNode("x/api", "api", SystemNodeKind.Service, "api:1")], [], []);
            var dto = new GetSystemHandler(new FakeSource(g)).Handle(new GetSystemQuery(dir));

            Assert.NotNull(dto);
            Assert.Contains(dto!.Nodes, n => n.Kind == "service" && n.Name == "api");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Ревью #19 M1: эндпоинт/хендлер должен принимать и путь-к-файлу compose (источник его поддерживает).
    [Fact]
    public void Accepts_compose_file_path()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "archforge_sysf_" + Guid.NewGuid().ToString("N"))).FullName;
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, "services:\n  api: { image: api:1 }\n");
        try
        {
            var dto = new GetSystemHandler(new ComposeSystemSource()).Handle(new GetSystemQuery(f));
            Assert.NotNull(dto);
            Assert.Contains(dto!.Nodes, n => n.Name == "api");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Линза «дрейф»: флаг drift включает convention-проблемы; по умолчанию их нет (лёгкий скан).
    [Fact]
    public void Drift_flag_toggles_convention_lens()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "archforge_sysd_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var source = new FakeSource(new SystemGraph([new SystemNode("svc-a/app", "app", SystemNodeKind.Service)], [], []));
            var scanner = new DriftScanner(new ServiceAnalyzer(
                new FakeDiscovery([new DiscoveredService("Svc.A", Path.Combine(dir, "svc-a"), "docker-compose.yml")]),
                new FakeTypes([new RawType("RegisterHandler", "class", [], "(Dep d)", "Application", "x.cs")]),
                new TypeClassifier(ConventionPack.Default())));
            var handler = new GetSystemHandler(source, scanner);

            var off = handler.Handle(new GetSystemQuery(dir, Drift: false));
            Assert.True(off!.Problems is null || off.Problems.All(p => p.Category != ProblemCategory.Convention));

            var on = handler.Handle(new GetSystemQuery(dir, Drift: true));
            Assert.Contains(on!.Problems!, p => p is { Category: ProblemCategory.Convention, Severity: "info", Kind: "drift" });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Линза «слои»: флаг layers включает architecture-проблемы layer-violation; по умолчанию их нет.
    [Fact]
    public void Layers_flag_toggles_architecture_lens()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "archforge_sysl_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var source = new FakeSource(new SystemGraph([new SystemNode("svc-a/app", "app", SystemNodeKind.Service)], [], []));
            var analyzer = new ServiceAnalyzer(
                new FakeDiscovery([new DiscoveredService("Svc.A", Path.Combine(dir, "svc-a"), "docker-compose.yml")]),
                new FakeTypes(
                [
                    new RawType("Order", "class", [], "(PaymentGateway g)", "Domain", "Order.cs"),
                    new RawType("PaymentGateway", "class", [], "()", "Infrastructure", "PaymentGateway.cs"),
                ]),
                new TypeClassifier(ConventionPack.Default()));
            var handler = new GetSystemHandler(source, layers: new LayerViolationScanner(analyzer));

            var off = handler.Handle(new GetSystemQuery(dir, Layers: false));
            Assert.True(off!.Problems is null || off.Problems.All(p => p.Kind != "layer-violation"));

            var on = handler.Handle(new GetSystemQuery(dir, Layers: true));
            Assert.Contains(on!.Problems!, p => p is { Kind: "layer-violation", Severity: "warning",
                Category: ProblemCategory.Architecture });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Дрейф гоняется только для пути-папки (Directory.Exists); путь-к-файлу compose не запускает разбор кода.
    [Fact]
    public void Drift_flag_ignored_for_file_path()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "archforge_sysdf_" + Guid.NewGuid().ToString("N"))).FullName;
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, "services:\n  api: { image: api:1 }\n");
        try
        {
            // сканер с дрейфующим сервисом: для пути-к-ФАЙЛУ линза дрейфа не включается → convention-проблем нет
            var scanner = new DriftScanner(new ServiceAnalyzer(
                new FakeDiscovery([new DiscoveredService("X", Path.Combine(dir, "x"), "docker-compose.yml")]),
                new FakeTypes([new RawType("RegisterHandler", "class", [], "(Dep d)", "Application", "x.cs")]),
                new TypeClassifier(ConventionPack.Default())));
            var dto = new GetSystemHandler(new ComposeSystemSource(), scanner).Handle(new GetSystemQuery(f, Drift: true));

            Assert.NotNull(dto);
            Assert.True(dto!.Problems is null || dto.Problems.All(p => p.Category != ProblemCategory.Convention));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
