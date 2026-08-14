using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;
using Xunit.Abstractions;

namespace ArchForge.Analysis.Tests.Systems;

public class ComposeSystemSourceTests(ITestOutputHelper output)
{
    private const string Workspace = "/Users/123kapefa/Documents/projects/product-site-workspace";
    private const string RealAuthCompose = Workspace + "/auth-service/docker-compose.yml";

    // ---- helpers ----

    private static string WriteCompose(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_compose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, yaml);
        return f;
    }

    // Ребро по ИМЕНАМ узлов (id теперь namespaced — <system>/<svc>).
    private static bool HasEdge(SystemGraph g, string srcName, string dstName, string kind)
    {
        var byId = g.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        return g.Edges.Any(e => e.Kind == kind
            && byId.TryGetValue(e.Source, out var s) && s.Name == srcName
            && byId.TryGetValue(e.Target, out var d) && d.Name == dstName);
    }

    // ---- unit-фикстуры ----

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
            Assert.True(HasEdge(g, "api", "app-db", "rw-db"));
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
            Assert.Contains(g.Problems, p =>
                p.Kind == "shared-db" && g.Nodes.Any(n => n.Id == p.Location && n.Name == "shared-db"));
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
            Assert.True(HasEdge(g, "api", "${CATALOG_DB_CONNECTION}", "rw-db"));
            Assert.Contains(g.Edges, e => e is { Deferred: true, Target: "${CATALOG_DB_CONNECTION}" });
        }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); }
    }

    // Находка ревью #1: одинаковые имена сервисов в РАЗНЫХ compose не должны сливаться в один узел.
    [Fact]
    public void Same_service_names_in_different_composes_do_not_merge()
    {
        var root = Path.Combine(Path.GetTempPath(), "archforge_multi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sysA"));
        Directory.CreateDirectory(Path.Combine(root, "sysB"));
        File.WriteAllText(Path.Combine(root, "sysA", "docker-compose.yml"), """
            services:
              api: { image: ghcr.io/ns/app-a:1, environment: { ConnectionStrings__Db: "Host=db" } }
              db:  { image: postgres:16 }
            """);
        File.WriteAllText(Path.Combine(root, "sysB", "docker-compose.yml"), """
            services:
              api: { image: ghcr.io/ns/app-b:1, environment: { ConnectionStrings__Db: "Host=db" } }
              db:  { image: postgres:16 }
            """);
        try
        {
            var g = new ComposeSystemSource().Read(root);
            Assert.Equal(2, g.Nodes.Count(n => n.Name == "api")); // две разные системы — два узла
            Assert.Equal(2, g.Nodes.Count(n => n.Name == "db"));
            Assert.Empty(g.Problems); // каждый api пишет в СВОЮ db → не общая
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Находка ревью #4: exporter-образ (postgres-exporter) — это сервис мониторинга, а не Datastore.
    [Fact]
    public void Exporter_image_is_not_classified_as_datastore()
    {
        var f = WriteCompose("""
            services:
              pg-exporter: { image: quay.io/community/postgres-exporter:v0.17.1 }
              db:          { image: postgres:16 }
            """);
        try
        {
            var g = new ComposeSystemSource().Read(f);
            Assert.Contains(g.Nodes, n => n.Name == "pg-exporter" && n.Kind == SystemNodeKind.Service);
            Assert.Contains(g.Nodes, n => n.Name == "db" && n.Kind == SystemNodeKind.Datastore);
        }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); }
    }

    // Находка ревью #3: два build-only сервиса (без образа) в одну БД → НЕ выдумываем общую БД.
    [Fact]
    public void Build_only_writers_do_not_fabricate_shared_db()
    {
        var f = WriteCompose("""
            services:
              api:     { build: ., environment: { ConnectionStrings__Db: "Host=db" } }
              migrate: { build: ., environment: { ConnectionStrings__Db: "Host=db" } }
              db:      { image: postgres:16 }
            """);
        try
        {
            var g = new ComposeSystemSource().Read(f);
            Assert.Empty(g.Problems); // образов нет → идентичность неизвестна → не выдумываем
        }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); }
    }

    // ---- реальный таргет (де-риск, локально; на CI скипается) ----

    [SkippableFact]
    public void Real_target_auth_service_honest_graph()
    {
        Skip.IfNot(File.Exists(RealAuthCompose), "таргет product-site-workspace не найден");

        var g = new ComposeSystemSource().Read(RealAuthCompose);

        Assert.Contains(g.Nodes, n => n.Name == "auth-api" && n.Kind == SystemNodeKind.Service);
        Assert.Contains(g.Nodes, n => n.Name == "postgres-auth" && n.Kind == SystemNodeKind.Datastore);
        Assert.Contains(g.Nodes, n => n.Name == "redis-auth" && n.Kind == SystemNodeKind.Datastore);
        Assert.True(HasEdge(g, "auth-api", "postgres-auth", "rw-db"));
        Assert.True(HasEdge(g, "auth-api", "redis-auth", "rw-db"));
        // auth-api + auth-migrate — один образ → это НЕ общая БД (детектор не выдумывает)
        Assert.DoesNotContain(g.Problems, p => p.Kind == "shared-db");
    }

    [SkippableFact]
    public void Real_workspace_multi_service_discovery()
    {
        Skip.IfNot(Directory.Exists(Workspace), "таргет product-site-workspace не найден");

        var g = new ComposeSystemSource().Read(Workspace);
        var services = g.Nodes.Count(n => n.Kind == SystemNodeKind.Service);
        var stores = g.Nodes.Count(n => n.Kind == SystemNodeKind.Datastore);
        output.WriteLine($"сервисов={services} хранилищ={stores} рёбер={g.Edges.Count} проблем={g.Problems.Count}");
        foreach (var p in g.Problems) output.WriteLine($"  ⚠ {p.Severity} {p.Kind}: {p.Title}");

        Assert.True(services >= 2 && stores >= 2); // мульти-сервисный discovery без падения
        // Честно: общих РЕЛЯЦИОННЫХ БД нет (у каждого сервиса своя postgres, exporter'ы исключены) —
        // ни одного Critical; но несколько сервисов делят один redis → shared-store (Warning), не прячем.
        Assert.DoesNotContain(g.Problems, p => p.Severity == ProblemSeverity.Critical);
        Assert.Contains(g.Problems, p => p.Kind == "shared-store"); // общий redis
    }

    // Находка ревью #1: два сервиса с ОДИНАКОВЫМ именем solution (Server.slnx) в разных папках не сливаются.
    [Fact]
    public void Same_solution_name_in_different_folders_do_not_merge()
    {
        var root = Path.Combine(Path.GetTempPath(), "archforge_slncol_" + Guid.NewGuid().ToString("N"));
        MakeSvc(root, "svc-a", "Server.slnx", "ghcr.io/ns/svc-a:1");
        MakeSvc(root, "svc-b", "Server.slnx", "ghcr.io/ns/svc-b:1");
        try
        {
            var g = new ComposeSystemSource().Read(root);
            Assert.Equal(2, g.Nodes.Count(n => n.Name == "api")); // namespace по папке → два разных узла
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void MakeSvc(string root, string dir, string sln, string apiImage)
    {
        var p = Directory.CreateDirectory(Path.Combine(root, dir)).FullName;
        File.WriteAllText(Path.Combine(p, sln), "<Solution/>");
        File.WriteAllText(Path.Combine(p, "docker-compose.yml"),
            $"services:\n  api: {{ image: {apiImage}, environment: {{ ConnectionStrings__Db: \"Host=db\" }} }}\n  db: {{ image: postgres:16 }}\n");
    }
}
