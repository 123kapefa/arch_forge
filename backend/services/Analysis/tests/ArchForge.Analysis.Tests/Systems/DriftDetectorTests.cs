using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class DriftDetectorTests
{
    private static readonly TypeClassifier Classifier = new();

    // «RegisterHandler» без маркера ICommandHandler → эвристическая роль + дрейф.
    private static readonly RawType[] Drifting = [new("RegisterHandler", "class", [], "(Dep d)", "Application", "x.cs")];
    // команда с маркером ICommand → роль по маркеру, дрейфа нет.
    private static readonly RawType[] Clean = [new("CreateWidgetCommand", "record", ["ICommand"], "(string N)", "Application", "x.cs")];

    private static ServiceAnalysis Ok(string loc, RawType[] raw) =>
        new(new DiscoveredService(loc, "/" + loc, "compose.yml"), loc, Classifier.Classify(raw, ConventionPack.Default()));
    private static ServiceAnalysis Failed(string loc) =>
        new(new DiscoveredService(loc, "/" + loc, "compose.yml"), loc, null);

    [Fact]
    public void Drifting_service_gives_convention_info()
    {
        var p = Assert.Single(DriftDetector.Detect([Ok("svc-a", Drifting)]));
        Assert.Equal(ProblemSeverity.Info, p.Severity);            // мягко — не красный
        Assert.Equal("drift", p.Kind);
        Assert.Equal(ProblemCategory.Convention, p.Category);
        Assert.Equal("svc-a", p.Location);
        Assert.Contains("svc-a", p.Title);
        var it = Assert.Single(p.Items!);              // отдельный факт дрейфа — в Items (тип · слой · заметка)
        Assert.Equal("RegisterHandler", it.Name);
        Assert.Equal("Application", it.Layer);
        Assert.Contains("ожидается", it.Note);
    }

    [Fact]
    public void Clean_service_gives_nothing() =>
        Assert.Empty(DriftDetector.Detect([Ok("c", Clean)]));

    [Fact]
    public void Failed_service_reports_coverage_not_silence()
    {
        var p = Assert.Single(DriftDetector.Detect([Failed("bad")]));
        Assert.Equal("drift-scan-failed", p.Kind);
        Assert.Equal(ProblemSeverity.Coverage, p.Severity);
        Assert.Equal(ProblemCategory.Convention, p.Category);
    }

    [Fact]
    public void Multiple_services_preserve_order()
    {
        var problems = DriftDetector.Detect([Ok("a", Drifting), Ok("b", Drifting)]);
        Assert.Equal(new[] { "a", "b" }, problems.Select(p => p.Location));
    }

    [Fact]
    public void Mixed_batch_reports_failure_and_drift()
    {
        // сбой и дрейф в одном батче обрабатываются независимо (per-service)
        var problems = DriftDetector.Detect([Failed("bad"), Ok("good", Drifting)]);
        Assert.Contains(problems, p => p is { Kind: "drift-scan-failed", Location: "bad" });
        Assert.Contains(problems, p => p is { Kind: "drift", Location: "good" });
    }
}
