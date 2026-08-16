using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class ServiceSummaryBuilderTests
{
    private static readonly TypeClassifier Classifier = new();

    private static ServiceAnalysis Svc(string loc, RawType[] raw) =>
        new(new DiscoveredService(loc, "/" + loc, "compose.yml"), loc, Classifier.Classify(raw, ConventionPack.Default()));
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
    public void Drift_does_not_color_layer_health()
    {
        // High-фикс ревью #26: дрейф — конвенция, НЕ красит health (инвариант линз, adr/0008), только заметка.
        RawType[] raw = [new("RegisterHandler", "class", [], "(Dep d)", "Application", "app.cs")]; // дрейф в Application
        var s = Assert.Single(ServiceSummaryBuilder.Build([Svc("s", raw)]));

        var app = s.Layers.Single(l => l.Name == "Application");
        Assert.Equal("ok", app.Health);            // дрейф не красит health
        Assert.Contains("расхождений", app.Note);   // но виден в заметке
        Assert.Equal("ok", s.Health);               // и сервис не warning из-за одного дрейфа
    }

    [Fact]
    public void Unknown_role_in_note_not_health()
    {
        RawType[] raw = [new("Thing", "class", [], "()", "Domain", "d.cs")]; // класс без роли/маркера → via unknown
        var s = Assert.Single(ServiceSummaryBuilder.Build([Svc("s", raw)]));

        var domain = s.Layers.Single(l => l.Name == "Domain");
        Assert.Equal("ok", domain.Health);          // coverage не красит health
        Assert.Contains("без роли", domain.Note);
    }

    [Fact]
    public void Ambiguous_type_name_across_layers_no_false_violation()
    {
        // одноимённый тип в двух слоях неоднозначен → не участвует в нарушении → без ложного warning
        RawType[] raw =
        [
            new("Foo", "class", [], "(Result r)", "Application", "Foo.cs"),
            new("Result", "class", [], "()", "Domain", "domain/Result.cs"),
            new("Result", "class", [], "()", "Infrastructure", "infra/Result.cs"),
        ];
        var s = Assert.Single(ServiceSummaryBuilder.Build([Svc("s", raw)]));
        Assert.All(s.Layers, l => Assert.Equal("ok", l.Health));
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
