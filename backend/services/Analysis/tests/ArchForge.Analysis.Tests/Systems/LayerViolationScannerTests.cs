using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class LayerViolationScannerTests
{
    private sealed class StubDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class StubTypes(Func<string, IReadOnlyList<RawType>> read) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => read(path);
    }

    // Order (Domain) с ctor-параметром PaymentGateway (Infrastructure) → ребро Order→PaymentGateway = нарушение.
    private static readonly RawType[] Violating =
    [
        new("Order", "class", [], "(PaymentGateway g)", "Domain", "Order.cs"),
        new("PaymentGateway", "class", [], "()", "Infrastructure", "PaymentGateway.cs"),
    ];
    // Здоровое направление: Infrastructure зависит от Domain.
    private static readonly RawType[] Healthy =
    [
        new("PaymentGateway", "class", [], "(Order o)", "Infrastructure", "PaymentGateway.cs"),
        new("Order", "class", [], "()", "Domain", "Order.cs"),
    ];

    private static DiscoveredService Svc(string root, string name) =>
        new(name, Path.Combine(root, name), "docker-compose.yml");

    private static LayerViolationScanner Scanner(IReadOnlyList<DiscoveredService> svcs, Func<string, IReadOnlyList<RawType>> read) =>
        new(new ServiceAnalyzer(new StubDiscovery(svcs), new StubTypes(read), new TypeClassifier(ConventionPack.Default())));

    private static string TempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_layer_" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Inner_to_outer_dependency_is_warning_in_architecture_lens()
    {
        var root = TempRoot();
        try
        {
            var p = Assert.Single(Scanner([Svc(root, "svc-a")], _ => Violating).Scan(root));
            Assert.Equal(ProblemSeverity.Warning, p.Severity);        // реальная арх-проблема
            Assert.Equal("layer-violation", p.Kind);
            Assert.Equal(ProblemCategory.Architecture, p.Category);   // линза «реальные проблемы»
            Assert.Equal("svc-a", p.Location);
            Assert.Contains("Order", p.Title);
            Assert.Contains("Infrastructure", p.Title);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Healthy_direction_yields_no_problem()
    {
        var root = TempRoot();
        try { Assert.Empty(Scanner([Svc(root, "svc-a")], _ => Healthy).Scan(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Broken_service_reports_coverage()
    {
        var root = TempRoot();
        try
        {
            var problems = Scanner([Svc(root, "bad")],
                _ => throw new IOException("boom")).Scan(root);
            Assert.Contains(problems, p => p is { Kind: "layer-scan-failed", Severity: ProblemSeverity.Coverage,
                Category: ProblemCategory.Architecture });
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
            Assert.Empty(Scanner(svcs, _ => Violating).Scan(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Ревью #23 High #1: одноимённый тип в РАЗНЫХ слоях неоднозначен → слой не судим → без ложного нарушения.
    [Fact]
    public void Ambiguous_type_name_across_layers_is_not_judged()
    {
        RawType[] ambiguous =
        [
            new("Foo", "class", [], "(Result r)", "Application", "Foo.cs"), // ребро Foo→Result
            new("Result", "class", [], "()", "Domain", "domain/Result.cs"),
            new("Result", "class", [], "()", "Infrastructure", "infra/Result.cs"), // тот же тип в 2 слоях
        ];
        var root = TempRoot();
        try { Assert.Empty(Scanner([Svc(root, "svc-a")], _ => ambiguous).Scan(root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Счётчик — полный, примеры урезаны до 3 (Take(3)).
    [Fact]
    public void Count_is_full_but_examples_truncated_to_three()
    {
        RawType[] four =
        [
            new("Hub", "class", [], "(A a, B b, C c, D d)", "Domain", "Hub.cs"), // 4 ребра в Infrastructure
            new("A", "class", [], "()", "Infrastructure", "A.cs"),
            new("B", "class", [], "()", "Infrastructure", "B.cs"),
            new("C", "class", [], "()", "Infrastructure", "C.cs"),
            new("D", "class", [], "()", "Infrastructure", "D.cs"),
        ];
        var root = TempRoot();
        try
        {
            var p = Assert.Single(Scanner([Svc(root, "svc-a")], _ => four).Scan(root));
            Assert.Contains("4 нарушений", p.Title);
            Assert.Equal(3, p.Title.Split('→').Length - 1); // ровно 3 примера-стрелки
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
