using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// G2 — монорепо с ОДНИМ корневым compose: discovery находит сервисы по .sln, но пер-сервисного
// compose у них нет. Анализ по ПАПКЕ должен откатываться на корневой compose, а не давать 0 узлов.
public class ComposeDiscoveryFallbackTests
{
    private sealed class NoComposeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private static void InTemp(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_g2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Falls_back_to_root_compose_when_discovered_services_have_no_compose() => InTemp(dir =>
    {
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
            services:
              api:
                image: app-api
              db:
                image: postgres:16
            """);
        // сервис найден по коду (.sln), но своего compose-файла у него нет → ComposePath = null
        var discovery = new NoComposeDiscovery([new DiscoveredService("Api", Path.Combine(dir, "Api"), null)]);

        var g = new ComposeSystemSource(discovery).Read(dir);

        Assert.Contains(g.Nodes, n => n.Name == "api");   // раньше здесь было 0 узлов
        Assert.Contains(g.Nodes, n => n.Name == "db" && n.Kind == SystemNodeKind.Datastore);
    });

    [Fact]
    public void Per_service_compose_still_wins_when_present() => InTemp(dir =>
    {
        var svcDir = Path.Combine(dir, "svc-a");
        Directory.CreateDirectory(svcDir);
        var svcCompose = Path.Combine(svcDir, "docker-compose.yml");
        File.WriteAllText(svcCompose, "services:\n  only-a:\n    image: app-a\n");
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), "services:\n  root-svc:\n    image: app-root\n");

        var discovery = new NoComposeDiscovery([new DiscoveredService("A", svcDir, svcCompose)]);

        var g = new ComposeSystemSource(discovery).Read(dir);

        Assert.Contains(g.Nodes, n => n.Name == "only-a");           // из пер-сервисного compose
        Assert.DoesNotContain(g.Nodes, n => n.Name == "root-svc");   // корневой fallback НЕ применён
    });

    // Смешанный кейс: у одного сервиса есть свой compose, у другого нет → используем пер-сервисные,
    // корневой fallback НЕ подмешиваем (раз хоть один per-service compose нашёлся).
    [Fact]
    public void Mixed_uses_per_service_and_ignores_root_when_any_has_compose() => InTemp(dir =>
    {
        var aDir = Path.Combine(dir, "svc-a");
        Directory.CreateDirectory(aDir);
        var aCompose = Path.Combine(aDir, "docker-compose.yml");
        File.WriteAllText(aCompose, "services:\n  only-a:\n    image: app-a\n");
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), "services:\n  root-svc:\n    image: app-root\n");

        var discovery = new NoComposeDiscovery(
        [
            new DiscoveredService("A", aDir, aCompose),
            new DiscoveredService("B", Path.Combine(dir, "svc-b"), null), // без своего compose
        ]);

        var g = new ComposeSystemSource(discovery).Read(dir);

        Assert.Contains(g.Nodes, n => n.Name == "only-a");
        Assert.DoesNotContain(g.Nodes, n => n.Name == "root-svc"); // есть пер-сервисный → fallback не подмешиваем
    });
}
