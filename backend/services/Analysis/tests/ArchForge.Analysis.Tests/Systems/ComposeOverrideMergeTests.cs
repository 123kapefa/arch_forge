using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// E9.1 слой 2 — наложение дефолтного overlay (docker-compose.override.yml) при разборе, как делает
// сам docker compose. Merge на «сыром» словаре → влияет и на deploy (порты/hc/сети), и на рёбра (env).
public class ComposeOverrideMergeTests
{
    private static SystemGraph Read(string baseYaml, string? overrideYaml,
        string baseName = "docker-compose.yml", string overrideName = "docker-compose.override.yml")
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_ovr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, baseName);
        File.WriteAllText(basePath, baseYaml);
        if (overrideYaml is not null) File.WriteAllText(Path.Combine(dir, overrideName), overrideYaml);
        return new ComposeSystemSource().Read(basePath); // путь к самому base — LoadCompose найдёт overlay-сосед
    }

    private static SystemNode Node(SystemGraph g, string name) => g.Nodes.Single(n => n.Name == name);

    [Fact]
    public void Override_publishes_port_absent_in_base()
    {
        // base: БД без опубликованного порта (внутренняя); override (как dev) выставляет её наружу
        var g = Read("""
            services:
              orders-db:
                image: postgres:17
            """, """
            services:
              orders-db:
                ports:
                  - "5432:5432"
            """);
        var db = Node(g, "orders-db");
        Assert.Contains(db.Deploy!.Ports, p => p.Container == "5432" && p.Host == "5432" && p.Published);
        Assert.Contains(g.Problems, p => p.Kind == "exposed-datastore" && p.Location == db.Id); // теперь наружу → находка
    }

    [Fact]
    public void Override_ports_concatenate_with_base()
    {
        var g = Read("""
            services:
              api:
                image: app-api
                ports:
                  - "8080:80"
            """, """
            services:
              api:
                ports:
                  - "9090:90"
            """);
        var ports = Node(g, "api").Deploy!.Ports;
        Assert.Contains(ports, p => p.Host == "8080" && p.Container == "80");
        Assert.Contains(ports, p => p.Host == "9090" && p.Container == "90"); // склеены, не заменены
    }

    [Fact]
    public void Override_environment_merges_and_creates_edge()
    {
        // base без строки подключения; override добавляет её → появляется ребро rw-db api → db
        var g = Read("""
            services:
              api:
                image: app-api
              orders-db:
                image: postgres:17
            """, """
            services:
              api:
                environment:
                  ConnectionStrings__Orders: "Host=orders-db;Database=orders"
            """);
        var api = Node(g, "api");
        var db = Node(g, "orders-db");
        Assert.Contains(g.Edges, e => e.Source == api.Id && e.Target == db.Id && e.Kind == "rw-db");
    }

    [Fact]
    public void Override_adds_healthcheck()
    {
        var g = Read("""
            services:
              api:
                image: app-api
            """, """
            services:
              api:
                healthcheck:
                  test: ["CMD", "curl", "-f", "http://localhost/health"]
            """);
        Assert.True(Node(g, "api").Deploy!.HasHealthcheck);
    }

    [Fact]
    public void Override_disables_base_healthcheck()
    {
        // healthcheck — map: override-ключ disable:true мерджится поверх base и отключает проверку
        var g = Read("""
            services:
              api:
                image: app-api
                healthcheck:
                  test: ["CMD", "curl", "-f", "http://localhost/health"]
            """, """
            services:
              api:
                healthcheck:
                  disable: true
            """);
        Assert.False(Node(g, "api").Deploy!.HasHealthcheck);
    }

    [Fact]
    public void Override_networks_union()
    {
        var g = Read("""
            services:
              api:
                image: app-api
                networks: [backend-net]
            """, """
            services:
              api:
                networks: [edge-net]
            """);
        var nets = Node(g, "api").Deploy!.Networks;
        Assert.Contains("backend-net", nets);
        Assert.Contains("edge-net", nets);
    }

    [Fact]
    public void Override_adds_new_service()
    {
        var g = Read("""
            services:
              api:
                image: app-api
            """, """
            services:
              worker:
                image: app-worker
            """);
        Assert.Contains(g.Nodes, n => n.Name == "worker");
        Assert.Contains(g.Nodes, n => n.Name == "api");
    }

    [Fact]
    public void No_override_file_leaves_base_unchanged()
    {
        var g = Read("""
            services:
              api:
                image: app-api
                ports:
                  - "8080:80"
            """, overrideYaml: null);
        Assert.Single(Node(g, "api").Deploy!.Ports);
    }

    [Fact]
    public void Compose_yaml_stem_override_applies()
    {
        // stem-вариант: compose.yaml + compose.override.yaml
        var g = Read("""
            services:
              api:
                image: app-api
                ports:
                  - "8080:80"
            """, """
            services:
              api:
                ports:
                  - "9090:90"
            """, baseName: "compose.yaml", overrideName: "compose.override.yaml");
        var ports = Node(g, "api").Deploy!.Ports;
        Assert.Equal(2, ports.Count);
    }

    [Fact]
    public void Broken_override_does_not_lose_valid_base()
    {
        // битый override (незакрытая flow-последовательность) не должен ронять разбор валидного base
        var g = Read("""
            services:
              api:
                image: app-api
                ports:
                  - "8080:80"
            """, "services:\n  api:\n    image: [a, b\n");
        var api = Node(g, "api");                                   // base уцелел
        Assert.Single(api.Deploy!.Ports);                           // override проигнорирован, base как есть
    }

    [Fact]
    public void Override_environment_list_form_merges_by_key_and_keeps_base_edge()
    {
        // base с environment в СПИСОЧНОЙ форме (строка подключения → ребро rw-db); override добавляет свою
        // переменную (тоже список). Merge по ключу не должен затереть строку подключения base.
        var g = Read("""
            services:
              api:
                image: app-api
                environment:
                  - "ConnectionStrings__Orders=Host=orders-db;Database=orders"
              orders-db:
                image: postgres:17
            """, """
            services:
              api:
                environment:
                  - "Feature__Flag=true"
            """);
        var api = Node(g, "api");
        var db = Node(g, "orders-db");
        Assert.Contains(g.Edges, e => e.Source == api.Id && e.Target == db.Id && e.Kind == "rw-db");
    }

    [Fact]
    public void Override_environment_mixed_forms_merge_by_key()
    {
        // base — map-форма, override — списочная: обе нормализуются к map, мерджатся по ключу (ребро base уцелело)
        var g = Read("""
            services:
              api:
                image: app-api
                environment:
                  ConnectionStrings__Orders: "Host=orders-db;Database=orders"
              orders-db:
                image: postgres:17
            """, """
            services:
              api:
                environment:
                  - "Feature__Flag=true"
            """);
        var api = Node(g, "api");
        var db = Node(g, "orders-db");
        Assert.Contains(g.Edges, e => e.Source == api.Id && e.Target == db.Id && e.Kind == "rw-db");
    }

    [Fact]
    public void Override_environment_base_list_override_map_merge_by_key()
    {
        // обратная комбинация: base — список, override — map; нормализация симметрична, ребро base уцелело
        var g = Read("""
            services:
              api:
                image: app-api
                environment:
                  - "ConnectionStrings__Orders=Host=orders-db;Database=orders"
              orders-db:
                image: postgres:17
            """, """
            services:
              api:
                environment:
                  Feature__Flag: "true"
            """);
        var api = Node(g, "api");
        var db = Node(g, "orders-db");
        Assert.Contains(g.Edges, e => e.Source == api.Id && e.Target == db.Id && e.Kind == "rw-db");
    }

    [Fact]
    public void Named_env_overlay_is_not_applied_by_default()
    {
        // docker-compose.prod.yml — именованная среда, БЕЗ явного выбора не применяется (часть 2 слоя)
        var g = Read("""
            services:
              api:
                image: app-api
                ports:
                  - "8080:80"
            """, """
            services:
              api:
                ports:
                  - "9090:90"
            """, overrideName: "docker-compose.prod.yml");
        Assert.Single(Node(g, "api").Deploy!.Ports); // prod-overlay проигнорирован по умолчанию
    }
}
