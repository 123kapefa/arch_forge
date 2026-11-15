using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// E9.2 ч.2 — серьёзность «хранилище наружу» зависит от среды; источник находки — файл:строка записи ports.
public class ExposedPortEnvTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "archforge_exposedenv_" + Guid.NewGuid().ToString("N"));
    public ExposedPortEnvTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    // base: БД без портов; dev публикует на все интерфейсы; prod — тоже (строка 5 — запись порта).
    private void Setup()
    {
        Write("docker-compose.yml", """
            services:
              orders-db:
                image: postgres:17
              orders-api:
                image: app-api
            """);
        Write("docker-compose.dev.yml", """
            services:
              orders-db:
                ports:
                  - "5432:5432"
            """);
        Write("docker-compose.prod.yml", """
            services:
              orders-db:
                image: postgres:17
                ports:
                  - "127.0.0.1:6000:6000"
                  - "5432:5432"
            """);
        Write("docker-compose.stage.yml", """
            services:
              orders-db:
                ports: ["5432:5432"]
            """);
    }

    private SystemProblem? Exposed(string? env) =>
        new ComposeSystemSource().Read(Path.Combine(_dir, "docker-compose.yml"), env)
            .Problems.FirstOrDefault(p => p.Kind == "exposed-datastore");

    [Fact]
    public void Prod_is_critical_with_file_and_line_of_the_port_entry()
    {
        Setup();
        var p = Exposed("prod");
        Assert.NotNull(p);
        Assert.Equal(ProblemSeverity.Critical, p!.Severity);
        var item = Assert.Single(p.Items!);
        Assert.Equal("docker-compose.prod.yml", item.File);   // относительный путь, не абсолютный
        Assert.Equal(6, item.Line);                           // строка записи "5432:5432" (не localhost-6000)
        Assert.Contains("prod", p.Title);
    }

    [Fact]
    public void Dev_is_not_a_finding()
    {
        Setup();
        Assert.Null(Exposed("dev"));
    }

    [Fact]
    public void Stage_and_default_stay_warning_without_claiming_prod()
    {
        Setup();
        Assert.Equal(ProblemSeverity.Warning, Exposed("stage")!.Severity);
        // default (base без портов + нет override) → портов наружу нет вовсе
        Assert.Null(Exposed(null));
    }

    [Fact]
    public void Default_environment_with_published_port_in_base_is_warning_with_base_source()
    {
        Write("docker-compose.yml", "services:\n  db:\n    image: redis:7\n    ports:\n      - \"6379:6379\"\n");
        Write("docker-compose.prod.yml", "services:\n  db:\n    image: redis:7\n");
        var p = Exposed(null);
        Assert.Equal(ProblemSeverity.Warning, p!.Severity);
        var item = Assert.Single(p.Items!);
        Assert.Equal("docker-compose.yml", item.File);
        Assert.Equal(5, item.Line);
        // а в prod порт из base тоже наследуется (merge) → Critical, источник — base-файл
        var prod = Exposed("prod");
        Assert.Equal(ProblemSeverity.Critical, prod!.Severity);
        Assert.Equal("docker-compose.yml", prod.Items![0].File);
    }

    [Theory]
    [InlineData("prod", EnvKind.Production)]
    [InlineData("Production", EnvKind.Production)]
    [InlineData("dev", EnvKind.Development)]
    [InlineData("local", EnvKind.Development)]
    [InlineData("stage", EnvKind.Other)]
    [InlineData(null, EnvKind.Other)]
    public void Environment_class_by_name(string? name, EnvKind expected) =>
        Assert.Equal(expected, EnvironmentClass.Of(name));

    [Fact]
    public void Detector_uses_active_environment_of_graph_and_skips_dev()
    {
        var node = new SystemNode("s/db", "db", SystemNodeKind.Datastore, "postgres:17",
            new DeployInfo([new PortMapping("5432", "5432", null, true)], false, []));
        SystemGraph G(string env) => new([node], [], [], [new SystemEnvironment("default", false), new SystemEnvironment(env, true)]);
        Assert.Equal(ProblemSeverity.Critical, Assert.Single(ExposedPortDetector.Detect(G("prod"))).Severity);
        Assert.Empty(ExposedPortDetector.Detect(G("dev")));
    }

    [Fact]
    public void Safe_overlay_entry_does_not_steal_the_source_of_the_published_base_entry()
    {
        Write("docker-compose.yml", "services:\n  db:\n    image: postgres:17\n    ports:\n      - \"5432:5432\"\n");
        Write("docker-compose.prod.yml", "services:\n  db:\n    ports:\n      - \"127.0.0.1:5432:5432\"\n");
        var p = Exposed("prod");
        var item = Assert.Single(p!.Items!);
        Assert.Equal("docker-compose.yml", item.File);   // а не безопасная строка оверлея
        Assert.Equal(5, item.Line);
    }

    [Fact]
    public void Hostile_yaml_aliases_do_not_crash_or_hang()
    {
        // цикл алиасов в записи ports и в x-расширении + вложенные алиасы («billion laughs»)
        var laughs = string.Join("\n", new[] { "x0: &x0 [a,a,a,a,a,a,a,a,a,a]" }.Concat(Enumerable.Range(1, 9).Select(i =>
            $"x{i}: &x{i} [{string.Join(",", Enumerable.Repeat($"*x{i - 1}", 10))}]")));
        Write("docker-compose.yml", "x-r: &r {k: *r}\n" + laughs + "\nservices:\n  db:\n    image: postgres:17\n    ports:\n      - *r\n      - *x9\n      - \"5432:5432\"\n");
        Write("docker-compose.prod.yml", "x-r: &r {k: *r}\nservices:\n  db:\n    ports:\n      - *r\n      - \"5432:5432\"\n");
        var graph = Task.Run(() => new ComposeSystemSource().Read(Path.Combine(_dir, "docker-compose.yml"), "prod")).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        Assert.NotNull(graph);   // главное — процесс жив (раньше: переполнение стека)
    }

    [Fact]
    public void Non_numeric_port_value_from_env_is_not_printed_in_title()
    {
        Write(".env", "DB_PORT=TOP-SECRET-VALUE\n");
        Write("docker-compose.yml", "services:\n  db:\n    image: postgres:17\n    ports:\n      - \"${DB_PORT}:5432\"\n");
        var p = Exposed(null);
        Assert.NotNull(p);
        Assert.DoesNotContain("TOP-SECRET-VALUE", p!.Title + string.Join("", p.Items!.Select(i => i.Name + i.Note)));
    }
}
