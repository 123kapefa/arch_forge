using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class SharedKernelBuilderTests
{
    private static readonly TypeClassifier Classifier = new();

    private static ServiceAnalysis Analysis(params RawType[] raw) =>
        new(new DiscoveredService("svc", "/repo", "compose.yml"), "backend", Classifier.Classify(raw, ConventionPack.Default()));

    [Fact]
    public void Builds_shared_node_with_root_and_layer()
    {
        var raw = new[]
        {
            new RawType("TokenForwardingHandler", "class", [], "()", "Shared", "/repo/backend/src/Shared/Kalmykov.Shared.AspNetCore/Token.cs"),
            new RawType("ICurrentUser", "interface", [], "", "Shared", "/repo/backend/src/Shared/Kalmykov.Shared.SharedKernel/User.cs"),
        };
        var built = SharedKernelBuilder.Build([Analysis(raw)]);
        Assert.NotNull(built);
        var (node, entry) = built!.Value;
        Assert.Equal("shared/kernel", node.Id);
        Assert.Equal("Shared kernel", node.Name);
        Assert.Equal(SystemNodeKind.Service, node.Kind);            // svc-card с drill по layers
        Assert.Equal("/repo/backend/src/Shared", entry.Root);       // корень для /api/map (весь shared kernel)
        var layer = Assert.Single(entry.Layers);
        Assert.Equal("Shared", layer.Name);
        Assert.Equal(2, layer.Types);
    }

    [Fact]
    public void No_shared_types_no_node()
    {
        var raw = new[] { new RawType("Order", "class", [], "()", "Domain", "/repo/backend/src/Services/Orders/Order.cs") };
        Assert.Null(SharedKernelBuilder.Build([Analysis(raw)]));
    }

    [Fact]
    public void Failed_analysis_is_skipped()
    {
        var failed = new ServiceAnalysis(new DiscoveredService("svc", "/repo", "compose.yml"), "backend", null);
        Assert.Null(SharedKernelBuilder.Build([failed]));
    }
}
