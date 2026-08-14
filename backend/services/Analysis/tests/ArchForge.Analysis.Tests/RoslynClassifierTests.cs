using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Roslyn;
using Xunit;

namespace ArchForge.Analysis.Tests;

public class RoslynClassifierTests
{
    [Fact]
    public void Marker_gives_role_without_drift()
    {
        var raw = new[]
        {
            new RawType("CreateWidgetCommand", "record", new[] { "ICommand" }, "(string Name)", "Application", "x.cs"),
            new RawType("CreateWidgetHandler", "class", new[] { "ICommandHandler" }, "(IRepo r)", "Application", "x.cs"),
        };
        var result = new TypeClassifier(ConventionPack.Default()).Classify(raw);
        Assert.Equal("Command", result.Types.First(t => t.Name == "CreateWidgetCommand").Role);
        Assert.Equal("Handler", result.Types.First(t => t.Name == "CreateWidgetHandler").Role);
        Assert.Empty(result.Drift);
    }

    [Fact]
    public void Drift_when_handler_lacks_marker()
    {
        var raw = new[] { new RawType("RegisterHandler", "class", Array.Empty<string>(), "(Dep d)", "Application", "x.cs") };
        var result = new TypeClassifier(ConventionPack.Default()).Classify(raw);
        Assert.Equal("heuristic", result.Types[0].Via);
        Assert.NotEmpty(result.Drift);
    }

    [Fact]
    public void Uses_edge_from_ctor_between_own_types()
    {
        var raw = new[]
        {
            new RawType("Account", "class", new[] { "IdentityUser" }, "(string e)", "Domain", "a.cs"),
            new RawType("RegisterHandler", "class", new[] { "ICommandHandler" }, "(Account acc)", "Application", "r.cs"),
        };
        var result = new TypeClassifier(ConventionPack.Default()).Classify(raw);
        Assert.Contains(result.Edges, e => e is { From: "RegisterHandler", To: "Account", Kind: "uses" });
    }

    [Fact]
    public void Reproduces_spike_coverage_on_auth_service()
    {
        const string target = "/Users/123kapefa/Documents/projects/microservices/auth_service";
        if (!Directory.Exists(target)) return; // локальный таргет — скип, если нет

        var raw = new RoslynTypeSource().Read(target);
        var result = new TypeClassifier(ConventionPack.Default()).Classify(raw);

        int total = Math.Max(result.Types.Count, 1);
        int classified = result.Coverage.Marker + result.Coverage.Heuristic;
        Assert.True(classified * 100 / total >= 90, $"покрытие {classified}/{total}");
        Assert.True(result.Drift.Count >= 4, $"дрейф {result.Drift.Count}");
    }
}
