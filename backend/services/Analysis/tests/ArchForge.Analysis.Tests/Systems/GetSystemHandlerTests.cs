using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class GetSystemHandlerTests
{
    private sealed class FakeSource(SystemGraph graph) : ISystemSource
    {
        public SystemGraph Read(string root, CancellationToken ct = default) => graph;
    }

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
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "archforge_sysh_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var g = new SystemGraph([new SystemNode("x/api", "api", SystemNodeKind.Service, "api:1")], [], []);
            var dto = new GetSystemHandler(new FakeSource(g)).Handle(new GetSystemQuery(dir));

            Assert.NotNull(dto);
            Assert.Contains(dto!.Nodes, n => n.Kind == "service" && n.Name == "api");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
