using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// G2 — монорепо с ОДНИМ корневым compose: discovery находит сервисы по .csproj, но пер-сервисного
// compose у них нет. Анализ по ПАПКЕ должен откатываться на корневой compose, а не давать 0 узлов.
public class ComposeDiscoveryFallbackTests
{
    private sealed class NoComposeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    [Fact]
    public void Falls_back_to_root_compose_when_discovered_services_have_no_compose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_g2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
            services:
              api:
                image: app-api
              db:
                image: postgres:16
            """);

        // сервис найден по коду (.csproj), но своего compose-файла у него нет → ComposePath = null
        var discovery = new NoComposeDiscovery([new DiscoveredService("Api", Path.Combine(dir, "Api"), null)]);

        var g = new ComposeSystemSource(discovery).Read(dir);

        Assert.Contains(g.Nodes, n => n.Name == "api");   // раньше здесь было 0 узлов
        Assert.Contains(g.Nodes, n => n.Name == "db" && n.Kind == SystemNodeKind.Datastore);
    }

    [Fact]
    public void Per_service_compose_still_wins_when_present()
    {
        // если у сервиса ЕСТЬ свой compose — используем его, корневой fallback не подмешиваем
        var dir = Path.Combine(Path.GetTempPath(), "archforge_g2b_" + Guid.NewGuid().ToString("N"));
        var svcDir = Path.Combine(dir, "svc-a");
        Directory.CreateDirectory(svcDir);
        var svcCompose = Path.Combine(svcDir, "docker-compose.yml");
        File.WriteAllText(svcCompose, "services:\n  only-a:\n    image: app-a\n");
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), "services:\n  root-svc:\n    image: app-root\n");

        var discovery = new NoComposeDiscovery([new DiscoveredService("A", svcDir, svcCompose)]);

        var g = new ComposeSystemSource(discovery).Read(dir);

        Assert.Contains(g.Nodes, n => n.Name == "only-a");           // из пер-сервисного compose
        Assert.DoesNotContain(g.Nodes, n => n.Name == "root-svc");   // корневой fallback НЕ применён
    }
}
