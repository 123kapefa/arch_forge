using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class LayerViolationDetectorTests
{
    private static readonly TypeClassifier Classifier = new(ConventionPack.Default());

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

    private static ServiceAnalysis Ok(string loc, RawType[] raw) =>
        new(new DiscoveredService(loc, "/" + loc, "compose.yml"), loc, Classifier.Classify(raw));
    private static ServiceAnalysis Failed(string loc) =>
        new(new DiscoveredService(loc, "/" + loc, "compose.yml"), loc, null);

    [Fact]
    public void Inner_to_outer_dependency_is_warning_in_architecture_lens()
    {
        var p = Assert.Single(LayerViolationDetector.Detect([Ok("svc-a", Violating)]));
        Assert.Equal(ProblemSeverity.Warning, p.Severity);        // реальная арх-проблема
        Assert.Equal("layer-violation", p.Kind);
        Assert.Equal(ProblemCategory.Architecture, p.Category);
        Assert.Equal("svc-a", p.Location);
        Assert.Contains("Order", p.Title);
        Assert.Contains("Infrastructure", p.Title);
    }

    [Fact]
    public void Healthy_direction_yields_no_problem() =>
        Assert.Empty(LayerViolationDetector.Detect([Ok("svc-a", Healthy)]));

    [Fact]
    public void Failed_service_reports_coverage()
    {
        var p = Assert.Single(LayerViolationDetector.Detect([Failed("bad")]));
        Assert.Equal("layer-scan-failed", p.Kind);
        Assert.Equal(ProblemSeverity.Coverage, p.Severity);
        Assert.Equal(ProblemCategory.Architecture, p.Category);
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
        Assert.Empty(LayerViolationDetector.Detect([Ok("svc-a", ambiguous)]));
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
        var p = Assert.Single(LayerViolationDetector.Detect([Ok("svc-a", four)]));
        Assert.Contains("4 нарушений", p.Title);
        Assert.Equal(3, p.Title.Split('→').Length - 1); // ровно 3 примера-стрелки
    }
}
