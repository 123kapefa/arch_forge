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
        var result = new TypeClassifier().Classify(raw, ConventionPack.Default());
        Assert.Equal("Command", result.Types.First(t => t.Name == "CreateWidgetCommand").Role);
        Assert.Equal("Handler", result.Types.First(t => t.Name == "CreateWidgetHandler").Role);
        Assert.Empty(result.Drift);
    }

    [Fact]
    public void Drift_when_handler_lacks_marker()
    {
        var raw = new[] { new RawType("RegisterHandler", "class", Array.Empty<string>(), "(Dep d)", "Application", "x.cs") };
        var result = new TypeClassifier().Classify(raw, ConventionPack.Default());
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
        var result = new TypeClassifier().Classify(raw, ConventionPack.Default());
        Assert.Contains(result.Edges, e => e is { From: "RegisterHandler", To: "Account", Kind: "uses" });
    }

    [SkippableFact]
    public void Reproduces_spike_coverage_on_auth_service()
    {
        string target = Environment.GetEnvironmentVariable("ARCHFORGE_AUTH_SERVICE")
            ?? "/Users/123kapefa/Documents/projects/microservices/auth_service";
        // Честный Skip (а не тихий проход), если локального таргета нет — напр. на CI.
        Skip.IfNot(Directory.Exists(target),
            $"нет локального таргета auth_service ({target}); задайте ARCHFORGE_AUTH_SERVICE");

        var raw = new RoslynTypeSource().Read(target);
        var result = new TypeClassifier().Classify(raw, ConventionPack.Default());

        int total = Math.Max(result.Types.Count, 1);
        int classified = result.Coverage.Marker + result.Coverage.Heuristic;
        Assert.True(classified * 100 / total >= 90, $"покрытие {classified}/{total}");
        Assert.True(result.Drift.Count >= 4, $"дрейф {result.Drift.Count}");
    }
}
