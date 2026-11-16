using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// E5.1 — провенанс ребра: файл:строка + evidence («что увидели», без значений) + основание.
public class EdgeOriginTests
{
    private static SystemGraph Read(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_e51_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "docker-compose.yml");
            File.WriteAllText(f, yaml);
            return new ComposeSystemSource().Read(f);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Yaml = """
        services:
          api:
            image: app-api
            depends_on: [pg]
            environment:
              ConnectionStrings__Db: "Host=pg;Password=topsecret"
              Orders__BaseUrl: "http://orders:8080"
          orders:
            image: app-orders
          pg:
            image: postgres:16
        """;

    [Fact]
    public void Compose_edges_carry_origin_with_file_line_and_no_secret_values()
    {
        var g = Read(Yaml);
        var api = g.Nodes.Single(n => n.Name == "api").Id;

        var db = g.Edges.Single(e => e.Source == api && e.Kind == "rw-db");
        Assert.NotNull(db.Origin);
        Assert.Equal("docker-compose.yml", db.Origin!.File);
        Assert.Equal(2, db.Origin.Line);   // строка ключа сервиса api
        Assert.Contains("ConnectionStrings__Db", db.Origin.Evidence);
        Assert.Equal("declared", db.Origin.Basis);

        var http = g.Edges.Single(e => e.Source == api && e.Kind == "http-call");
        Assert.Equal("name", http.Origin!.Basis);   // цель определена по имени хоста — не «подтверждено»
        Assert.Contains("Orders__BaseUrl", http.Origin.Evidence);

        // секрет из строки подключения не попадает в evidence ни одного ребра
        Assert.DoesNotContain(g.Edges, e => e.Origin?.Evidence.Contains("topsecret") == true);
    }

    [Fact]
    public void Origin_is_mapped_to_dto_and_omitted_when_absent()
    {
        var e = new SystemEdge("a", "b", "rw-db", EdgeVia.Config, 0.9, Origin: new EdgeOrigin("c.yml", 3, "ev", "declared")).ToEdgeDto();
        Assert.Equal(("c.yml", 3, "ev", "declared"), (e.Origin!.File, e.Origin.Line, e.Origin.Evidence, e.Origin.Basis));
        Assert.Null(new SystemEdge("a", "b", "rw-db", EdgeVia.Config, 0.9).ToEdgeDto().Origin);
        Assert.DoesNotContain("origin", System.Text.Json.JsonSerializer.Serialize(new SystemEdge("a", "b", "rw-db", EdgeVia.Config, 0.9).ToEdgeDto()), StringComparison.OrdinalIgnoreCase);
    }
}
