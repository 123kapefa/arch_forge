using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

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

    // Ревью #19 M1: эндпоинт/хендлер должен принимать и путь-к-файлу compose (источник его поддерживает).
    [Fact]
    public void Accepts_compose_file_path()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "archforge_sysf_" + Guid.NewGuid().ToString("N"))).FullName;
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, "services:\n  api: { image: api:1 }\n");
        try
        {
            var dto = new GetSystemHandler(new ComposeSystemSource()).Handle(new GetSystemQuery(f));
            Assert.NotNull(dto);
            Assert.Contains(dto!.Nodes, n => n.Name == "api");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
