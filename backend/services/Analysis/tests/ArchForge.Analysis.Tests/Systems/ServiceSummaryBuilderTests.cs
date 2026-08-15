using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class ServiceSummaryBuilderTests
{
    private static readonly TypeClassifier Classifier = new(ConventionPack.Default());

    private static ServiceAnalysis Svc(string loc, RawType[] raw) =>
        new(new DiscoveredService(loc, "/" + loc, "compose.yml"), loc, Classifier.Classify(raw));
    private static ServiceAnalysis Failed(string loc) =>
        new(new DiscoveredService(loc, "/" + loc, "compose.yml"), loc, null);

    [Fact]
    public void Summarizes_layers_with_per_layer_health()
    {
        RawType[] raw =
        [
            new("Order", "class", [], "(PaymentGateway g)", "Domain", "Order.cs"), // Domain→Infrastructure нарушение
            new("PaymentGateway", "class", [], "()", "Infrastructure", "PaymentGateway.cs"),
            new("CreateWidgetCommand", "record", ["ICommand"], "(string N)", "Application", "app.cs"),
        ];
        var s = Assert.Single(ServiceSummaryBuilder.Build([Svc("svc-a", raw)]));

        Assert.Equal("svc-a", s.Id);
        Assert.Equal("warning", s.Health); // из-за нарушения направления слоя
        var domain = s.Layers.Single(l => l.Name == "Domain");
        Assert.Equal("warning", domain.Health);
        Assert.Contains("→ Infrastructure", domain.Note);
        Assert.Equal("ok", s.Layers.Single(l => l.Name == "Application").Health);
    }

    [Fact]
    public void Clean_service_is_all_ok()
    {
        RawType[] raw =
        [
            new("CreateWidgetCommand", "record", ["ICommand"], "(string N)", "Application", "app.cs"),
            new("Widget", "class", [], "()", "Domain", "d.cs"),
        ];
        var s = Assert.Single(ServiceSummaryBuilder.Build([Svc("s", raw)]));
        Assert.Equal("ok", s.Health);
        Assert.All(s.Layers, l => Assert.Equal("ok", l.Health));
    }

    [Fact]
    public void Layers_in_canonical_order()
    {
        RawType[] raw =
        [
            new("C", "class", [], "()", "Contracts", "C.cs"),
            new("D", "class", [], "()", "Domain", "D.cs"),
            new("A", "class", [], "()", "Api", "A.cs"),
        ];
        var s = Assert.Single(ServiceSummaryBuilder.Build([Svc("s", raw)]));
        Assert.Equal(new[] { "API", "Domain", "Contracts" }, s.Layers.Select(l => l.Name));
    }

    [Fact]
    public void Failed_service_absent_from_summary()
    {
        Assert.Empty(ServiceSummaryBuilder.Build([Failed("bad")]));
    }

    [Fact]
    public void Service_without_recognizable_layers_absent()
    {
        // только tests/unknown слои → судить нечего → карточку не выдумываем
        RawType[] raw = [new("Foo", "class", [], "()", "unknown", "foo.cs")];
        Assert.Empty(ServiceSummaryBuilder.Build([Svc("s", raw)]));
    }
}
