using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// E9.2 — детектор «хранилище опубликовано наружу»: БД/кэш с портом на все интерфейсы (0.0.0.0 / ::),
// localhost-bind и обычные сервисы не флагаются.
// Юнит-тесты собирают SystemGraph напрямую (как SharedDbDetectorTests/DanglingEventDetectorTests),
// чтобы проверять именно логику детектора; сквозной прогон через ComposeSystemSource — один smoke-тест.
public class ExposedPortDetectorTests
{
    private static SystemGraph WithDatastore(params PortMapping[] ports) => new(
        [new SystemNode("orders/db", "db", SystemNodeKind.Datastore, "postgres:17",
            new DeployInfo(ports, HasHealthcheck: false, Networks: []))],
        [], []);

    private static bool Exposed(SystemGraph g) =>
        ExposedPortDetector.Detect(g).Any(p => p.Kind == "exposed-datastore" && p.Location == "orders/db");

    [Theory]
    [InlineData(null)]        // host_ip не задан → 0.0.0.0 по умолчанию
    [InlineData("")]          // пустая строка — то же
    [InlineData("0.0.0.0")]   // все интерфейсы явно
    [InlineData("::")]        // все интерфейсы IPv6
    public void Flags_datastore_published_to_all_interfaces(string? hostIp)
    {
        var g = WithDatastore(new PortMapping("5432", "5432", hostIp, Published: true));
        Assert.True(Exposed(g));
    }

    [Theory]
    [InlineData("127.0.0.1")] // localhost — доступно только с самой машины
    [InlineData("::1")]       // localhost IPv6
    public void Does_not_flag_datastore_bound_to_localhost(string hostIp)
    {
        var g = WithDatastore(new PortMapping("5432", "5432", hostIp, Published: true));
        Assert.False(Exposed(g));
    }

    [Fact]
    public void Flags_ephemeral_published_port_without_host()
    {
        // короткая форма "5432" = публикация на случайный хост-порт на всех интерфейсах
        var g = WithDatastore(new PortMapping("5432", Host: null, HostIp: null, Published: true));
        Assert.True(Exposed(g));
    }

    [Fact]
    public void Does_not_flag_unpublished_port()
    {
        // long-form с одним target (без published) → внутренний порт, не наружу
        var g = WithDatastore(new PortMapping("5432", Host: null, HostIp: null, Published: false));
        Assert.False(Exposed(g));
    }

    [Fact]
    public void Emits_one_finding_per_node_even_with_several_exposed_ports()
    {
        var g = WithDatastore(
            new PortMapping("5432", "5432", null, Published: true),
            new PortMapping("5433", "5433", "0.0.0.0", Published: true));
        Assert.Single(ExposedPortDetector.Detect(g), p => p.Location == "orders/db");
    }

    [Fact]
    public void Does_not_flag_non_datastore_service()
    {
        // у сервиса/gateway публикация порта наружу нормальна
        var g = new SystemGraph(
            [new SystemNode("orders/api", "api", SystemNodeKind.Service, "app-api",
                new DeployInfo([new PortMapping("80", "8080", null, Published: true)], false, []))],
            [], []);
        Assert.Empty(ExposedPortDetector.Detect(g));
    }

    [Fact]
    public void Does_not_flag_datastore_without_deploy_or_ports()
    {
        Assert.Empty(ExposedPortDetector.Detect(WithDatastore()));            // Deploy есть, портов нет
        var noDeploy = new SystemGraph(
            [new SystemNode("orders/db", "db", SystemNodeKind.Datastore, "postgres:17")], [], []);
        Assert.Empty(ExposedPortDetector.Detect(noDeploy));                   // Deploy == null
    }

    [Fact]
    public void Smoke_wired_through_compose_source()
    {
        // сквозная проводка: YAML → ComposeSystemSource → DeployInfo → детектор → graph.Problems
        var dir = Path.Combine(Path.GetTempPath(), "archforge_exposed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, """
            services:
              orders-db:
                image: postgres:17
                ports:
                  - "5432:5432"
            """);
        var g = new ComposeSystemSource().Read(f);
        Assert.Contains(g.Problems, p => p.Kind == "exposed-datastore"
            && g.Nodes.Any(n => n.Id == p.Location && n.Name == "orders-db"));
    }
}
