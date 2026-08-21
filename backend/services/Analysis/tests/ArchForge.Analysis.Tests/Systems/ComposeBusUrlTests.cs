using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// G6 — amqp://-строка подключения в ConnectionStrings__* → ребро uses-bus к шине (rabbitmq не «isolated»).
public class ComposeBusUrlTests
{
    private static SystemGraph Read(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_g6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "docker-compose.yml");
            File.WriteAllText(f, yaml);
            return new ComposeSystemSource().Read(f);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Amqp_connection_string_creates_uses_bus_edge()
    {
        var g = Read("""
            services:
              api:
                image: app-api
                environment:
                  ConnectionStrings__RabbitMq: "amqp://guest:guest@rabbitmq:5672"
              rabbitmq:
                image: rabbitmq:4-management
            """);

        var api = g.Nodes.Single(n => n.Name == "api").Id;
        var mq = g.Nodes.Single(n => n.Name == "rabbitmq");
        Assert.Equal(SystemNodeKind.Topic, mq.Kind);
        Assert.Contains(g.Edges, e => e.Source == api && e.Target == mq.Id && e.Kind == "uses-bus");
        Assert.DoesNotContain(g.Problems, p => p.Kind == "isolated-topic"); // шина больше не изолирована
    }

    [Fact]
    public void Uppercase_connection_string_key_recognized()  // в .env/env_file ключи обычно UPPERCASE
    {
        var g = Read("""
            services:
              api:
                image: app-api
                environment:
                  CONNECTIONSTRINGS__RABBITMQ: "amqp://guest:guest@rabbitmq:5672"
              rabbitmq:
                image: rabbitmq:4-management
            """);

        var api = g.Nodes.Single(n => n.Name == "api").Id;
        var mq = g.Nodes.Single(n => n.Name == "rabbitmq");
        Assert.Contains(g.Edges, e => e.Source == api && e.Target == mq.Id && e.Kind == "uses-bus");
    }

    [Fact]
    public void Redis_url_connection_string_creates_rw_db_edge()
    {
        var g = Read("""
            services:
              api:
                image: app-api
                environment:
                  ConnectionStrings__Cache: "redis://cache:6379"
              cache:
                image: redis:7
            """);

        var api = g.Nodes.Single(n => n.Name == "api").Id;
        var cache = g.Nodes.Single(n => n.Name == "cache").Id;
        Assert.Contains(g.Edges, e => e.Source == api && e.Target == cache && e.Kind == "rw-db"); // redis:// → rw-db
    }
}
