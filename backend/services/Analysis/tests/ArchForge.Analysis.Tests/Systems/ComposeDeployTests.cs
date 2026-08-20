using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// E9.1 — парсинг DevOps-атрибутов сервиса из compose: порты (публичность/host_ip), healthcheck, сети.
public class ComposeDeployTests
{
    private static SystemNode ReadSvc(string yaml, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_deploy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "docker-compose.yml");
        File.WriteAllText(f, yaml);
        return new ComposeSystemSource().Read(f).Nodes.Single(n => n.Name == name);
    }

    [Fact]
    public void Parses_short_form_ports_with_publish_and_host_ip()
    {
        var api = ReadSvc("""
            services:
              api:
                image: ghcr.io/ns/app-api
                ports:
                  - "8080:80"
                  - "127.0.0.1:5432:5432"
                  - "9090"
                  - "6000:6000/udp"
            """, "api");
        var p = api.Deploy!.Ports;
        Assert.Contains(p, x => x.Container == "80" && x.Host == "8080" && x.HostIp == null && x.Published);
        Assert.Contains(p, x => x.Container == "5432" && x.Host == "5432" && x.HostIp == "127.0.0.1" && x.Published);
        Assert.Contains(p, x => x.Container == "9090" && x.Host == null && x.Published);   // эфемерный хост-порт
        Assert.Contains(p, x => x.Container == "6000" && x.Host == "6000");                // /udp отброшен
    }

    [Fact]
    public void Parses_long_form_ports_published_flag()
    {
        var api = ReadSvc("""
            services:
              api:
                image: app-api
                ports:
                  - target: 5432
                    published: 5432
                    host_ip: 127.0.0.1
                  - target: 8080
            """, "api");
        var p = api.Deploy!.Ports;
        Assert.Contains(p, x => x.Container == "5432" && x.Host == "5432" && x.HostIp == "127.0.0.1" && x.Published);
        Assert.Contains(p, x => x.Container == "8080" && x.Host == null && !x.Published);   // только target → не опубликован
    }

    [Fact]
    public void Interpolates_port_variables()
    {
        var api = ReadSvc("""
            services:
              api:
                image: app-api
                ports:
                  - "${API_PORT:-8080}:80"
            """, "api");
        Assert.Contains(api.Deploy!.Ports, x => x.Host == "8080" && x.Container == "80");
    }

    [Fact]
    public void Detects_healthcheck_presence_and_disable()
    {
        Assert.True(ReadSvc("""
            services:
              api:
                image: app-api
                healthcheck:
                  test: ["CMD", "curl", "-f", "http://localhost/health"]
                  interval: 10s
            """, "api").Deploy!.HasHealthcheck);

        Assert.False(ReadSvc("""
            services:
              api:
                image: app-api
                healthcheck:
                  disable: true
            """, "api").Deploy!.HasHealthcheck);

        Assert.False(ReadSvc("""
            services:
              api:
                image: app-api
            """, "api").Deploy!.HasHealthcheck);
    }

    [Fact]
    public void Parses_networks_list_and_map()
    {
        var list = ReadSvc("""
            services:
              api:
                image: app-api
                networks:
                  - backend-net
                  - edge-net
            """, "api");
        Assert.Equal(new[] { "backend-net", "edge-net" }, list.Deploy!.Networks);

        var map = ReadSvc("""
            services:
              api:
                image: app-api
                networks:
                  data-net:
                    aliases: [db]
            """, "api");
        Assert.Contains("data-net", map.Deploy!.Networks);
    }

    [Fact]
    public void No_deploy_sections_yields_empty_not_null()
    {
        var api = ReadSvc("""
            services:
              api:
                image: app-api
            """, "api");
        Assert.NotNull(api.Deploy);
        Assert.Empty(api.Deploy!.Ports);
        Assert.Empty(api.Deploy!.Networks);
        Assert.False(api.Deploy!.HasHealthcheck);
    }
}
