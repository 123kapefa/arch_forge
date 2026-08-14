using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;
using Xunit.Abstractions;

namespace ArchForge.Analysis.Tests.Systems;

public class ComposeSystemSourceTests(ITestOutputHelper output)
{
    private const string Workspace = "/Users/123kapefa/Documents/projects/product-site-workspace";
    private const string RealAuthCompose = Workspace + "/auth-service/docker-compose.yml";

    private static string WriteCompose(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_compose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, yaml);
        return f;
    }

    [Fact]
    public void Builds_services_datastores_and_rwdb_edges()
    {
        var f = WriteCompose("""
            services:
              api:
                image: ghcr.io/ns/app-api:${TAG:-stage}
                environment:
                  ConnectionStrings__Db: >-
                    Host=app-db;Port=5432;Database=${DB:-app};Password=${PW:?set}
                depends_on:
                  app-db:
                    condition: service_healthy
              app-db:
                image: postgres:17-alpine
            """);
        try
        {
            var g = new ComposeSystemSource().Read(f);
            Assert.Contains(g.Nodes, n => n.Name == "api" && n.Kind == SystemNodeKind.Service);
            Assert.Contains(g.Nodes, n => n.Name == "app-db" && n.Kind == SystemNodeKind.Datastore);
            Assert.Contains(g.Edges, e => e is { Source: "api", Target: "app-db", Kind: "rw-db" });
            Assert.Empty(g.Problems); // один писатель
        }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); }
    }

    [Fact]
    public void Detects_shared_db_when_two_apps_write_one()
    {
        var f = WriteCompose("""
            services:
              api-a:
                image: ghcr.io/ns/a:1
                environment: { ConnectionStrings__Db: "Host=shared-db;Database=x" }
              api-b:
                image: ghcr.io/ns/b:1
                environment: { ConnectionStrings__Db: "Host=shared-db;Database=x" }
              shared-db:
                image: postgres:16
            """);
        try
        {
            var g = new ComposeSystemSource().Read(f);
            Assert.Contains(g.Problems, p => p.Kind == "shared-db" && p.Location == "shared-db");
        }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); }
    }

    [Fact]
    public void Deferred_connection_becomes_external_node()
    {
        var f = WriteCompose("""
            services:
              api:
                image: app:1
                environment:
                  - ConnectionStrings__Db=${CATALOG_DB_CONNECTION}
            """);
        try
        {
            var g = new ComposeSystemSource().Read(f);
            Assert.Contains(g.Nodes, n => n.Kind == SystemNodeKind.External && n.Name == "${CATALOG_DB_CONNECTION}");
            Assert.Contains(g.Edges, e => e is { Deferred: true, Target: "${CATALOG_DB_CONNECTION}", Kind: "rw-db" });
        }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); }
    }

    // Реальный таргет: честный системный граф из настоящего compose auth-service (де-риск P1.5-0).
    [SkippableFact]
    public void Real_target_auth_service_honest_graph()
    {
        Skip.IfNot(File.Exists(RealAuthCompose), "таргет product-site-workspace не найден");

        var g = new ComposeSystemSource().Read(RealAuthCompose);

        Assert.Contains(g.Nodes, n => n.Name == "auth-api" && n.Kind == SystemNodeKind.Service);
        Assert.Contains(g.Nodes, n => n.Name == "postgres-auth" && n.Kind == SystemNodeKind.Datastore);
        Assert.Contains(g.Nodes, n => n.Name == "redis-auth" && n.Kind == SystemNodeKind.Datastore);
        Assert.Contains(g.Edges, e => e is { Source: "auth-api", Target: "postgres-auth", Kind: "rw-db" });
        Assert.Contains(g.Edges, e => e is { Source: "auth-api", Target: "redis-auth", Kind: "rw-db" });
        // auth-api + auth-migrate — один образ → это НЕ общая БД (детектор не выдумывает)
        Assert.DoesNotContain(g.Problems, p => p.Kind == "shared-db");
    }

    // Масштаб/робастность: разбор ВСЕГО messy-воркспейса (несколько compose) без падения.
    [SkippableFact]
    public void Real_workspace_multi_service_discovery()
    {
        Skip.IfNot(Directory.Exists(Workspace), "таргет product-site-workspace не найден");

        var g = new ComposeSystemSource().Read(Workspace);
        var services = g.Nodes.Count(n => n.Kind == SystemNodeKind.Service);
        var stores = g.Nodes.Count(n => n.Kind == SystemNodeKind.Datastore);
        output.WriteLine($"сервисов={services} хранилищ={stores} рёбер={g.Edges.Count} проблем={g.Problems.Count}");
        foreach (var p in g.Problems) output.WriteLine($"  ⚠ {p.Severity} {p.Kind}: {p.Title}");

        // мульти-сервисный discovery работает и не падает на реальном воркспейсе
        Assert.True(services >= 2 && stores >= 2);
        // NB: product_site использует docker-compose-dev.yaml (не базовое имя) → пока не подхватывается;
        // папочный discovery (P1.5-B) это закроет.
    }
}
