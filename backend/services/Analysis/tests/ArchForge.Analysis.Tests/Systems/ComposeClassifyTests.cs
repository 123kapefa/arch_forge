using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// G1 — распознавание образов хранилищ (pgvector/typesense/elasticsearch/…) как Datastore,
// иначе на реальных проектах основная БД (pgvector) виделась «сервисом» и не флагалась.
public class ComposeClassifyTests
{
    private static SystemNode ReadNode(string image, string svc = "svc")
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_cls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "docker-compose.yml");
            File.WriteAllText(f, $"services:\n  {svc}:\n    image: {image}\n");
            return new ComposeSystemSource().Read(f).Nodes.Single(n => n.Name == svc);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("pgvector/pgvector:pg16")]      // postgres+pgvector
    [InlineData("typesense/typesense:30.1")]    // поисковый индекс
    [InlineData("elasticsearch:8.13")]
    [InlineData("opensearchproject/opensearch:2")]
    [InlineData("clickhouse:24.1")]
    public void Recognizes_datastore_images(string image)
    {
        Assert.Equal(SystemNodeKind.Datastore, ReadNode(image).Kind);
    }

    [Fact]
    public void App_image_stays_service()
    {
        Assert.Equal(SystemNodeKind.Service, ReadNode("ghcr.io/ns/orders-api:1").Kind);
    }
}
