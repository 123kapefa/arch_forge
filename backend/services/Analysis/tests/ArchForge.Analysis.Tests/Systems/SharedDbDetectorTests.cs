using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Systems;

public class SharedDbDetectorTests
{
    [Fact]
    public void Fires_when_two_different_apps_write_one_datastore()
    {
        var g = new SystemGraph(
            [
                new SystemNode("a", "a", SystemNodeKind.Service, "ghcr.io/ns/app-a:1"),
                new SystemNode("b", "b", SystemNodeKind.Service, "ghcr.io/ns/app-b:1"),
                new SystemNode("db", "db", SystemNodeKind.Datastore, "postgres:17"),
            ],
            [
                new SystemEdge("a", "db", "rw-db", EdgeVia.Config, 0.9),
                new SystemEdge("b", "db", "rw-db", EdgeVia.Config, 0.9),
            ], []);

        Assert.Contains(SharedDbDetector.Detect(g), p => p.Kind == "shared-db" && p.Location == "db");
    }

    [Fact]
    public void Ignores_api_and_migrate_of_same_image()
    {
        var g = new SystemGraph(
            [
                new SystemNode("api", "api", SystemNodeKind.Service, "ghcr.io/ns/auth-api:stage"),
                new SystemNode("migrate", "migrate", SystemNodeKind.Service, "ghcr.io/ns/auth-api:stage"),
                new SystemNode("db", "db", SystemNodeKind.Datastore, "postgres:17"),
            ],
            [
                new SystemEdge("api", "db", "rw-db", EdgeVia.Config, 0.9),
                new SystemEdge("migrate", "db", "rw-db", EdgeVia.Compose, 0.7),
            ], []);

        Assert.Empty(SharedDbDetector.Detect(g));
    }

    [Fact]
    public void Ignores_deferred_edges()
    {
        var g = new SystemGraph(
            [
                new SystemNode("a", "a", SystemNodeKind.Service, "img-a"),
                new SystemNode("b", "b", SystemNodeKind.Service, "img-b"),
                new SystemNode("db", "db", SystemNodeKind.Datastore, "postgres"),
            ],
            [
                new SystemEdge("a", "db", "rw-db", EdgeVia.Config, 0.5, Deferred: true),
                new SystemEdge("b", "db", "rw-db", EdgeVia.Config, 0.5, Deferred: true),
            ], []);

        Assert.Empty(SharedDbDetector.Detect(g));
    }

    [Fact]
    public void Ignores_monitoring_exporter_writer()
    {
        var g = new SystemGraph(
            [
                new SystemNode("app", "app", SystemNodeKind.Service, "ghcr.io/ns/catalog-api:1"),
                new SystemNode("exp", "exp", SystemNodeKind.Service, "quay.io/community/postgres-exporter:v1"),
                new SystemNode("db", "db", SystemNodeKind.Datastore, "postgres:16"),
            ],
            [
                new SystemEdge("app", "db", "rw-db", EdgeVia.Config, 0.9),
                new SystemEdge("exp", "db", "rw-db", EdgeVia.Config, 0.9),
            ], []);

        Assert.Empty(SharedDbDetector.Detect(g)); // exporter — не приложение → один писатель
    }

    [Fact]
    public void Shared_redis_is_warning_shared_store_not_critical()
    {
        var g = new SystemGraph(
            [
                new SystemNode("a", "a", SystemNodeKind.Service, "app-a:1"),
                new SystemNode("b", "b", SystemNodeKind.Service, "app-b:1"),
                new SystemNode("cache", "cache", SystemNodeKind.Datastore, "redis:7"),
            ],
            [
                new SystemEdge("a", "cache", "rw-db", EdgeVia.Config, 0.9),
                new SystemEdge("b", "cache", "rw-db", EdgeVia.Config, 0.9),
            ], []);

        var p = SharedDbDetector.Detect(g);
        // сигнал связанности не прячем, но и не Critical: общий кэш — часто осознанно
        Assert.Contains(p, x => x.Kind == "shared-store" && x.Severity == ProblemSeverity.Warning);
        Assert.DoesNotContain(p, x => x.Severity == ProblemSeverity.Critical);
    }

    [Fact]
    public void Exporter_named_app_is_excluded_accepted_false_negative()
    {
        // Осознанный компромисс: приложение с образом *-exporter исключается из «пишущих». Если такое
        // приложение реально пишет в общую БД с другим — мы это пропустим (false negative), но не выдумываем.
        var g = new SystemGraph(
            [
                new SystemNode("app", "app", SystemNodeKind.Service, "ghcr.io/ns/orders-api:1"),
                new SystemNode("rep", "rep", SystemNodeKind.Service, "ghcr.io/ns/report-exporter:1"),
                new SystemNode("db", "db", SystemNodeKind.Datastore, "postgres:16"),
            ],
            [
                new SystemEdge("app", "db", "rw-db", EdgeVia.Config, 0.9),
                new SystemEdge("rep", "db", "rw-db", EdgeVia.Config, 0.9),
            ], []);

        Assert.Empty(SharedDbDetector.Detect(g)); // report-exporter исключён → один писатель → не флагаем
    }
}
