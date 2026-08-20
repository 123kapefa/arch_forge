using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// E9.1 слой 2 — именованные среды: выбор ?env=<name> накладывает docker-compose.<env>.yml (БЕЗ override.yml,
// как `docker compose -f base -f <env>`); default = base + override.yml. Список сред отдаётся в графе.
public class ComposeNamedEnvTests
{
    private static SystemGraph Read(string? env, params (string Name, string Yaml)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_env_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, yaml) in files) File.WriteAllText(Path.Combine(dir, name), yaml);
        return new ComposeSystemSource().Read(Path.Combine(dir, "docker-compose.yml"), env);
    }

    private const string Base = """
        services:
          api:
            image: app-api
            ports:
              - "8080:80"
        """;

    private static IReadOnlyList<string> Ports(SystemGraph g) =>
        g.Nodes.Single(n => n.Name == "api").Deploy!.Ports.Select(p => p.Host ?? p.Container).ToList();

    [Fact]
    public void No_overlays_no_environments()
    {
        var g = Read(null, ("docker-compose.yml", Base));
        Assert.Null(g.Environments); // одна base, выбора нет → поле опущено
    }

    [Fact]
    public void Override_only_is_not_a_named_choice()
    {
        // override применяется к default, но это не отдельная выбираемая среда → выбора по-прежнему нет
        var g = Read(null,
            ("docker-compose.yml", Base),
            ("docker-compose.override.yml", "services:\n  api:\n    ports: [\"9090:90\"]\n"));
        Assert.Null(g.Environments);
        Assert.Contains("9090", Ports(g)); // но override реально наложен на default
    }

    [Fact]
    public void Named_env_is_listed_with_default_active()
    {
        var g = Read(null,
            ("docker-compose.yml", Base),
            ("docker-compose.prod.yml", "services:\n  api:\n    ports: [\"5000:5000\"]\n"));
        Assert.NotNull(g.Environments);
        Assert.Equal(new[] { "default", "prod" }, g.Environments!.Select(e => e.Name).ToArray());
        Assert.Contains(g.Environments!, e => e is { Name: "default", Active: true });
        Assert.Contains(g.Environments!, e => e is { Name: "prod", Active: false });
    }

    [Fact]
    public void Selecting_named_env_applies_its_overlay()
    {
        var files = new (string, string)[]
        {
            ("docker-compose.yml", Base),
            ("docker-compose.prod.yml", "services:\n  api:\n    ports: [\"5000:5000\"]\n"),
        };
        var def = Read(null, files);
        Assert.DoesNotContain("5000", Ports(def));       // prod-оверлей по умолчанию не применён

        var prod = Read("prod", files);
        Assert.Contains("5000", Ports(prod));            // выбор prod → его оверлей наложен
        Assert.Contains("8080", Ports(prod));            // base сохранён
        Assert.Contains(prod.Environments!, e => e is { Name: "prod", Active: true });
    }

    [Fact]
    public void Named_env_excludes_default_override()
    {
        // env=prod = base + prod.yml, БЕЗ override.yml (как docker compose -f base -f prod)
        var files = new (string, string)[]
        {
            ("docker-compose.yml", Base),
            ("docker-compose.override.yml", "services:\n  api:\n    ports: [\"9090:90\"]\n"),
            ("docker-compose.prod.yml", "services:\n  api:\n    ports: [\"5000:5000\"]\n"),
        };
        var def = Read(null, files);
        Assert.Contains("9090", Ports(def));             // default → override наложен
        Assert.DoesNotContain("5000", Ports(def));

        var prod = Read("prod", files);
        Assert.Contains("5000", Ports(prod));            // prod → prod-оверлей
        Assert.DoesNotContain("9090", Ports(prod));      // override.yml НЕ наложен для именованной среды
    }

    [Fact]
    public void Unknown_env_falls_back_to_default()
    {
        var files = new (string, string)[]
        {
            ("docker-compose.yml", Base),
            ("docker-compose.prod.yml", "services:\n  api:\n    ports: [\"5000:5000\"]\n"),
        };
        var g = Read("staging", files);                  // среды "staging" нет → default
        Assert.DoesNotContain("5000", Ports(g));         // prod-оверлей не наложен
        Assert.Contains(g.Environments!, e => e is { Name: "default", Active: true });
        Assert.DoesNotContain(g.Environments!, e => e.Name == "staging"); // несуществующую не выдумываем
    }

    [Fact]
    public void Multiple_named_envs_sorted_default_first()
    {
        var g = Read(null,
            ("docker-compose.yml", Base),
            ("docker-compose.prod.yml", "services:\n  api:\n    ports: [\"5000:5000\"]\n"),
            ("docker-compose.dev.yml", "services:\n  api:\n    ports: [\"5001:5001\"]\n"));
        Assert.Equal(new[] { "default", "dev", "prod" }, g.Environments!.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Default_named_file_does_not_duplicate_default_env()
    {
        // docker-compose.default.yml — токен "default" зарезервирован; не создаёт вторую default-среду
        var g = Read(null,
            ("docker-compose.yml", Base),
            ("docker-compose.default.yml", "services:\n  api:\n    ports: [\"7000:7000\"]\n"));
        Assert.Null(g.Environments);            // именованной "default" нет → выбора нет → поле опущено
    }

    [Fact]
    public void Broken_named_overlay_does_not_lose_base()
    {
        var g = Read("prod",
            ("docker-compose.yml", Base),
            ("docker-compose.prod.yml", "services:\n  api:\n    image: [broken\n"));
        Assert.Contains("8080", Ports(g));      // битый prod-оверлей изолирован, base цел
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    [Fact]
    public void Named_envs_union_across_multiple_composes()
    {
        var root = Path.Combine(Path.GetTempPath(), "archforge_envws_" + Guid.NewGuid().ToString("N"));
        var aDir = Path.Combine(root, "svc-a");
        var bDir = Path.Combine(root, "svc-b");
        Directory.CreateDirectory(aDir);
        Directory.CreateDirectory(bDir);
        File.WriteAllText(Path.Combine(aDir, "docker-compose.yml"), "services:\n  a:\n    image: app-a\n    ports: [\"8080:80\"]\n");
        File.WriteAllText(Path.Combine(aDir, "docker-compose.dev.yml"), "services:\n  a:\n    ports: [\"5001:5001\"]\n");
        File.WriteAllText(Path.Combine(bDir, "docker-compose.yml"), "services:\n  b:\n    image: app-b\n");
        File.WriteAllText(Path.Combine(bDir, "docker-compose.prod.yml"), "services:\n  b:\n    ports: [\"5000:5000\"]\n");

        var discovery = new FakeDiscovery(
        [
            new DiscoveredService("svc-a", aDir, Path.Combine(aDir, "docker-compose.yml")),
            new DiscoveredService("svc-b", bDir, Path.Combine(bDir, "docker-compose.yml")),
        ]);

        // список сред — union по всем compose: default + dev (svc-a) + prod (svc-b)
        var all = new ComposeSystemSource(discovery).Read(root);
        Assert.Equal(new[] { "default", "dev", "prod" }, all.Environments!.Select(e => e.Name).ToArray());

        // выбор dev применяется к svc-a (у svc-b dev-оверлея нет → его base не трогается)
        var dev = new ComposeSystemSource(discovery).Read(root, "dev");
        Assert.Contains(dev.Nodes.Single(n => n.Name == "a").Deploy!.Ports, p => p.Host == "5001");
        Assert.Contains(dev.Environments!, e => e is { Name: "dev", Active: true });
    }
}
