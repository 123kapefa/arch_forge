using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

public class GetSystemHandlerTests
{
    private sealed class FakeSource(SystemGraph graph) : ISystemSource
    {
        public string? LastEnv { get; private set; }
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default)
        {
            LastEnv = env;
            return graph;
        }
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class FakeTypes(IReadOnlyList<RawType> raw) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => raw;
    }

    // считает вызовы Read — для проверки «один Roslyn-проход на сервис» при обеих линзах.
    private sealed class CountingTypes(IReadOnlyList<RawType> raw) : ITypeSource
    {
        public int Reads { get; private set; }
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) { Reads++; return raw; }
    }

    // ServiceAnalyzer с одним сервисом (svc-a) поверх заданных типов.
    private static ServiceAnalyzer Analyzer(string dir, params RawType[] raw) =>
        new(new FakeDiscovery([new DiscoveredService("Svc.A", Path.Combine(dir, "svc-a"), "docker-compose.yml")]),
            new FakeTypes(raw), new TypeClassifier());

    private static SystemGraph OneService() =>
        new([new SystemNode("svc-a/app", "app", SystemNodeKind.Service)], [], []);

    private static string TempDir(string tag) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"archforge_{tag}_" + Guid.NewGuid().ToString("N"))).FullName;

    private static readonly RawType Drift = new("RegisterHandler", "class", [], "(Dep d)", "Application", "x.cs");
    private static readonly RawType[] Layer =
    [
        new("Order", "class", [], "(PaymentGateway g)", "Domain", "Order.cs"),
        new("PaymentGateway", "class", [], "()", "Infrastructure", "PaymentGateway.cs"),
    ];

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
        var dir = TempDir("sysh");
        try
        {
            var g = new SystemGraph([new SystemNode("x/api", "api", SystemNodeKind.Service, "api:1")], [], []);
            var dto = new GetSystemHandler(new FakeSource(g)).Handle(new GetSystemQuery(dir));

            Assert.NotNull(dto);
            Assert.Contains(dto!.Nodes, n => n.Kind == "service" && n.Name == "api");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Ревью #19 M1: эндпоинт/хендлер принимает и путь-к-файлу compose.
    [Fact]
    public void Accepts_compose_file_path()
    {
        var dir = TempDir("sysf");
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

    // E9.1 слой 2: выбранная среда из запроса доходит до источника.
    [Fact]
    public void Env_is_forwarded_to_source()
    {
        var dir = TempDir("sysenv");
        try
        {
            var src = new FakeSource(OneService());
            new GetSystemHandler(src).Handle(new GetSystemQuery(dir, Env: "prod"));
            Assert.Equal("prod", src.LastEnv);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Drift_flag_toggles_convention_lens()
    {
        var dir = TempDir("sysd");
        try
        {
            var handler = new GetSystemHandler(new FakeSource(OneService()), Analyzer(dir, Drift));

            var off = handler.Handle(new GetSystemQuery(dir, Drift: false));
            Assert.True(off!.Problems is null || off.Problems.All(p => p.Category != ProblemCategory.Convention));

            var on = handler.Handle(new GetSystemQuery(dir, Drift: true));
            Assert.Contains(on!.Problems!, p => p is { Category: ProblemCategory.Convention, Severity: "info", Kind: "drift" });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Layers_flag_toggles_architecture_lens()
    {
        var dir = TempDir("sysl");
        try
        {
            var handler = new GetSystemHandler(new FakeSource(OneService()), Analyzer(dir, Layer));

            var off = handler.Handle(new GetSystemQuery(dir, Layers: false));
            Assert.True(off!.Problems is null || off.Problems.All(p => p.Kind != "layer-violation"));

            var on = handler.Handle(new GetSystemQuery(dir, Layers: true));
            Assert.Contains(on!.Problems!, p => p is { Kind: "layer-violation", Severity: "warning", Category: ProblemCategory.Architecture });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Обе линзы одновременно: ОДИН проход ServiceAnalyzer питает оба детектора (без двойного Roslyn).
    [Fact]
    public void Drift_and_layers_together_add_both_lenses()
    {
        var dir = TempDir("sysdl");
        try
        {
            var handler = new GetSystemHandler(new FakeSource(OneService()), Analyzer(dir, [Drift, .. Layer]));

            var dto = handler.Handle(new GetSystemQuery(dir, Drift: true, Layers: true));

            Assert.Contains(dto!.Problems!, p => p is { Kind: "drift", Category: ProblemCategory.Convention });
            Assert.Contains(dto.Problems!, p => p is { Kind: "layer-violation", Category: ProblemCategory.Architecture });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // L0-сводка слоёв (Services) приходит при код-линзах и отсутствует без них.
    [Fact]
    public void Service_summary_present_only_with_code_lens()
    {
        var dir = TempDir("syssum");
        try
        {
            var handler = new GetSystemHandler(new FakeSource(OneService()), Analyzer(dir, Layer));

            Assert.Null(handler.Handle(new GetSystemQuery(dir))!.Services); // без флагов — лёгкий скан, сводки нет

            var on = handler.Handle(new GetSystemQuery(dir, Layers: true));
            var summary = Assert.Single(on!.Services!);
            Assert.Contains(summary.Layers, l => l.Name == "Domain");

            // сводка приходит и по линзе дрейфа (не только слоёв)
            Assert.NotNull(handler.Handle(new GetSystemQuery(dir, Drift: true))!.Services);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Цель рефактора (ревью #24): при обеих линзах сервис разбирается РОВНО один раз, а не дважды.
    [Fact]
    public void Both_lenses_analyze_each_service_once()
    {
        var dir = TempDir("sys1x");
        try
        {
            var types = new CountingTypes([Drift, .. Layer]);
            var analyzer = new ServiceAnalyzer(
                new FakeDiscovery([new DiscoveredService("Svc.A", Path.Combine(dir, "svc-a"), "docker-compose.yml")]),
                types, new TypeClassifier());

            new GetSystemHandler(new FakeSource(OneService()), analyzer)
                .Handle(new GetSystemQuery(dir, Drift: true, Layers: true));

            Assert.Equal(1, types.Reads); // один Roslyn-проход на сервис, хотя линзы обе
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class ThrowingTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => throw new IOException("boom");
    }

    // P1.5-H: сервис не разобрался → в контракте видимая деградация «карта неполная», не тихий пропуск.
    [Fact]
    public void Failed_service_parse_surfaces_degradation()
    {
        var dir = TempDir("sysdeg");
        try
        {
            var analyzer = new ServiceAnalyzer(
                new FakeDiscovery([new DiscoveredService("Svc.A", Path.Combine(dir, "svc-a"), "docker-compose.yml")]),
                new ThrowingTypes(), new TypeClassifier());

            var dto = new GetSystemHandler(new FakeSource(OneService()), analyzer)
                .Handle(new GetSystemQuery(dir, Drift: true));

            Assert.NotNull(dto!.Degradations);
            Assert.Contains(dto.Degradations!, d => d.Contains("Svc.A"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Все сервисы разобрались → деградаций нет (поле опущено).
    [Fact]
    public void No_degradation_when_all_services_parse()
    {
        var dir = TempDir("sysok");
        try
        {
            var dto = new GetSystemHandler(new FakeSource(OneService()), Analyzer(dir, Drift))
                .Handle(new GetSystemQuery(dir, Drift: true));
            Assert.Null(dto!.Degradations);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Код-линзы гоняются только для пути-папки; путь-к-файлу compose не запускает разбор кода.
    [Fact]
    public void Code_lenses_ignored_for_file_path()
    {
        var dir = TempDir("sysfl");
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, "services:\n  api: { image: api:1 }\n");
        try
        {
            var dto = new GetSystemHandler(new ComposeSystemSource(), Analyzer(dir, Drift))
                .Handle(new GetSystemQuery(f, Drift: true, Layers: true));

            Assert.NotNull(dto);
            Assert.True(dto!.Problems is null || dto.Problems.All(p =>
                p.Category != ProblemCategory.Convention && p.Kind != "layer-violation"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
