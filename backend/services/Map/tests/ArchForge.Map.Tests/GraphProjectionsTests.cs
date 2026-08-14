using System;
using System.Linq;
using ArchForge.Contracts;
using ArchForge.Map.Core;
using Xunit;

namespace ArchForge.Map.Tests;

public class GraphProjectionsTests
{
    // A → B → C ; D — одиночка. Слои: A,B — domain; C,D — infra.
    private static GraphDto Sample() => new(
        Project: "demo",
        Nodes: new[]
        {
            new NodeDto("domain:A", "A", "class", "domain", "Aggregate", "marker", "A.cs", Array.Empty<string>()),
            new NodeDto("domain:B", "B", "class", "domain", "Entity", "heuristic", "B.cs", Array.Empty<string>()),
            new NodeDto("infra:C", "C", "class", "infra", "Repository", "marker", "C.cs", Array.Empty<string>()),
            new NodeDto("infra:D", "D", "class", "infra", "Service", "unknown", "D.cs", Array.Empty<string>()),
        },
        Edges: new[]
        {
            new EdgeDto("e1", "domain:A", "domain:B", "holds"),
            new EdgeDto("e2", "domain:B", "infra:C", "uses"),
        },
        Coverage: new CoverageDto(2, 1, 1, 4),
        Drift: Array.Empty<string>());

    [Fact]
    public void Focus_keeps_node_and_direct_neighbors()
    {
        GraphDto g = GraphProjections.Focus(Sample(), "domain:B");

        Assert.Equal(
            new[] { "domain:A", "domain:B", "infra:C" },
            g.Nodes.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, g.Edges.Count);
    }

    [Fact]
    public void Focus_drops_unrelated_and_indirect_nodes()
    {
        GraphDto g = GraphProjections.Focus(Sample(), "domain:A");

        Assert.DoesNotContain(g.Nodes, n => n.Id == "infra:D"); // не связан
        Assert.DoesNotContain(g.Nodes, n => n.Id == "infra:C"); // A—C нет прямого ребра
        Assert.Single(g.Edges);                                 // только A→B
    }

    [Fact]
    public void OnlyLayer_keeps_layer_and_internal_edges()
    {
        GraphDto g = GraphProjections.OnlyLayer(Sample(), "domain");

        Assert.Equal(2, g.Nodes.Count);
        Assert.All(g.Nodes, n => Assert.Equal("domain", n.Layer));
        Assert.Single(g.Edges); // A→B внутри domain; B→C (в infra) отброшено
    }

    [Fact]
    public void Projection_preserves_project_and_honest_coverage()
    {
        GraphDto g = GraphProjections.OnlyLayer(Sample(), "domain");

        Assert.Equal("demo", g.Project);
        Assert.Equal(4, g.Coverage.Total); // покрытие честное — по всему анализу, не по срезу
    }
}
