using System.Text.Json;
using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests.Systems;

// E9.3 — «чего не хватает в среде»: ключи конфигурации из кода против compose-environment и appsettings выбранной среды.
// Секреты: в находках только ИМЕНА ключей и пометка «секрет»; значения (из compose/appsettings/кода) нигде не появляются.
public class MissingEnvKeyTests : IDisposable
{
    private const string SecretValue = "S3cr3t-VALUE-must-never-leak";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "archforge_envkeys_" + Guid.NewGuid().ToString("N"));
    public MissingEnvKeyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string W(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    // Сервис orders: код читает 4 ключа; compose base задаёт Db (секрет, значение — SecretValue); prod задаёт ещё Jwt:Secret.
    private void Setup()
    {
        W("orders/docker-compose.yml", $"""
            services:
              orders-api:
                image: app-api
                environment:
                  ConnectionStrings__Db: "Host=db;Password={SecretValue}"
                  Feature__Flag: "on"
            """);
        W("orders/docker-compose.prod.yml", $"""
            services:
              orders-api:
                environment:
                  Jwt__Secret: {SecretValue}
            """);
        W("orders/src/Startup.cs", """
            public class Startup(Microsoft.Extensions.Configuration.IConfiguration configuration)
            {
                public void Run()
                {
                    var db = configuration.GetConnectionString("Db");
                    var jwt = configuration["Jwt:Secret"];
                    var flag = configuration["Feature:Flag"];
                    var mail = configuration.GetSection("Mail")["Host"];
                    var key = configuration.GetValue<string>("Payments:ApiKey");
                    var cache = new Dictionary<string,string>(); var x = cache["not-config"];
                }
            }
            """);
        W("orders/src/appsettings.json", $$"""{ "Mail": { "Host": "{{SecretValue}}" } }""");
        W("orders/src/appsettings.Development.json", """{ "Payments": { "ApiKey": "x" } }""");
    }

    private (SystemGraph Graph, IReadOnlyList<SystemProblem> Problems) Run(string? env)
    {
        var graph = new ComposeSystemSource().Read(Path.Combine(_dir, "orders", "docker-compose.yml"), env);
        var keys = new RoslynConfigKeySource().Read(Path.Combine(_dir, "orders"));
        var node = graph.Nodes.Single(n => n.Name == "orders-api");
        var problems = MissingEnvKeyDetector.Detect(graph, [("orders", [node.Id], keys)]);
        return (graph, problems);
    }

    [Fact]
    public void Source_reads_code_keys_from_config_receivers_only()
    {
        Setup();
        var keys = new RoslynConfigKeySource().Read(Path.Combine(_dir, "orders")).Used.Select(u => u.Key).ToList();
        Assert.Equivalent(new[] { "ConnectionStrings:Db", "Jwt:Secret", "Feature:Flag", "Mail:Host", "Payments:ApiKey" }, keys);
    }

    [Fact]
    public void Prod_reports_only_what_prod_lacks_with_secret_marks_and_code_location()
    {
        Setup();
        var (_, problems) = Run("prod");
        var p = Assert.Single(problems);
        Assert.Equal(MissingEnvKeyDetector.Kind, p.Kind);
        // Db и Feature:Flag — из base, Jwt:Secret — из prod-оверлея, Mail:Host — из appsettings.json. Не хватает только ключа,
        // объявленного лишь в appsettings.Development.json (в prod он не действует).
        var item = Assert.Single(p.Items!);
        Assert.Equal("Payments:ApiKey", item.Name);
        Assert.Equal("", item.Layer);                       // метка не в Layer
        Assert.StartsWith("секрет", item.Note);
        Assert.EndsWith("Startup.cs", item.File);
        Assert.True(item.Line > 0);
        Assert.Contains("prod", p.Title);
    }

    [Fact]
    public void Default_environment_lacks_prod_only_keys_and_dev_settings_do_not_count()
    {
        Setup();
        var (_, problems) = Run(null);
        var names = Assert.Single(problems).Items!.Select(i => i.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "Jwt:Secret", "Payments:ApiKey" }, names);   // Jwt задан только в prod-оверлее
    }

    [Fact]
    public void Development_environment_counts_appsettings_Development()
    {
        Setup();
        W("orders/docker-compose.dev.yml", "services:\n  orders-api:\n    environment:\n      Jwt__Secret: x\n");
        var (_, problems) = Run("dev");
        Assert.Empty(problems);   // Payments:ApiKey объявлен в appsettings.Development.json
    }

    [Fact]
    public void No_secret_value_reaches_findings_or_env_key_channel()
    {
        Setup();
        foreach (var env in new string?[] { null, "prod" })
        {
            var (graph, problems) = Run(env);
            var dump = JsonSerializer.Serialize(problems) + JsonSerializer.Serialize(graph.EnvKeys);
            Assert.DoesNotContain(SecretValue, dump);
            Assert.NotNull(graph.EnvKeys);   // имена есть…
            Assert.Contains("ConnectionStrings__Db", graph.EnvKeys!.Values.SelectMany(v => v));
        }
        // и источник ключей не несёт значений appsettings
        var declared = new RoslynConfigKeySource().Read(Path.Combine(_dir, "orders"));
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(declared));
    }

    [Theory]
    [InlineData("Jwt:Secret", true)]
    [InlineData("ConnectionStrings:Db", true)]
    [InlineData("Payments__ApiKey", true)]
    [InlineData("Smtp:Password", true)]
    [InlineData("Feature:Flag", false)]
    [InlineData("Mail:Host", false)]
    [InlineData("Game:Monkey", false)]
    [InlineData("Ui:Hotkey", false)]
    [InlineData("Combat:Assault", false)]
    public void Secret_by_name(string key, bool secret) => Assert.Equal(secret, SecretKeys.IsSecret(key));

    [Fact]
    public void Findings_are_capped_and_service_without_nodes_is_skipped()
    {
        var used = Enumerable.Range(0, 120).Select(i => new ConfigKeyUse($"K{i}", "s/a.cs", i + 1)).ToList();
        var keys = new ServiceConfigKeys(used, new Dictionary<string, IReadOnlySet<string>>());
        var g = new SystemGraph([new SystemNode("s/api", "api", SystemNodeKind.Service)], [], [],
            EnvKeys: new Dictionary<string, IReadOnlyList<string>> { ["s/api"] = [] });
        var p = Assert.Single(MissingEnvKeyDetector.Detect(g, [("s", ["s/api"], keys)]));
        Assert.Equal(50, p.Items!.Count);
        Assert.Contains("120", p.Title);
        Assert.Empty(MissingEnvKeyDetector.Detect(g, [("s", [], keys)]));
    }

    // ── сквозь обработчик системы ──
    private sealed class Discovery(string root) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string _) => [new("orders", Path.Combine(root, "orders"), Path.Combine(root, "orders", "docker-compose.yml"))];
    }
    private sealed class NoTypes : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => [];
    }

    [Fact]
    public void Handler_reports_missing_keys_for_the_selected_env_without_leaking_values()
    {
        Setup();
        var source = new ComposeSystemSource(new Discovery(_dir));
        var handler = new GetSystemHandler(source,
            new ServiceAnalyzer(new Discovery(_dir), new NoTypes(), new TypeClassifier()),
            configKeys: new RoslynConfigKeySource());

        var prod = handler.Handle(new GetSystemQuery(_dir, Layers: true, Env: "prod"))!;
        var p = Assert.Single(prod.Problems!, x => x.Kind == "missing-env-key");
        Assert.Equal("orders/orders-api", p.Location);
        Assert.Equal("Payments:ApiKey", Assert.Single(p.Items!).Name);
        Assert.NotNull(p.Fix);
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(prod));

        var def = handler.Handle(new GetSystemQuery(_dir, Layers: true))!;
        Assert.Equal(2, Assert.Single(def.Problems!, x => x.Kind == "missing-env-key").Items!.Count);

        // без линзы, требующей кода, разбор ключей не запускается
        Assert.DoesNotContain(handler.Handle(new GetSystemQuery(_dir, Env: "prod"))!.Problems ?? [], x => x.Kind == "missing-env-key");
    }

    // ── правки ревью ──

    [Fact]
    public void Passthrough_env_key_without_equals_counts_as_set_and_survives_overlay_merge()
    {
        W("orders/docker-compose.yml", "services:\n  orders-api:\n    image: app\n    environment:\n      - Jwt__Secret\n      - Feature__Flag=on\n");
        W("orders/docker-compose.prod.yml", "services:\n  orders-api:\n    environment:\n      - Mail__Host=h\n");
        W("orders/src/S.cs", "class S(Microsoft.Extensions.Configuration.IConfiguration configuration){ void R(){ var a=configuration[\"Jwt:Secret\"]; var b=configuration[\"Mail:Host\"]; var c=configuration[\"Feature:Flag\"]; } }");
        foreach (var env in new string?[] { null, "prod" })
        {
            var (_, problems) = Run(env);
            Assert.DoesNotContain(problems.SelectMany(p => p.Items!), i => i.Name == "Jwt:Secret");
        }
    }

    [Fact]
    public void Unreadable_env_file_suppresses_the_claim()
    {
        W("orders/docker-compose.yml", "services:\n  orders-api:\n    image: app\n    env_file: .env.prod\n");   // файла нет
        W("orders/src/S.cs", "class S(Microsoft.Extensions.Configuration.IConfiguration configuration){ void R(){ var a=configuration[\"Jwt:Secret\"]; } }");
        var (graph, problems) = Run(null);
        Assert.Empty(problems);
        Assert.Contains(graph.Nodes.Single().Id, graph.EnvIncomplete!);
    }

    [Fact]
    public void Keys_with_fallback_and_platform_keys_are_not_required()
    {
        W("orders/docker-compose.yml", "services:\n  orders-api:\n    image: app\n");
        W("orders/src/S.cs", """
            class S(Microsoft.Extensions.Configuration.IConfiguration configuration)
            {
                void R()
                {
                    var a = configuration["Opt:A"] ?? "x";
                    var b = configuration.GetValue<int>("Opt:B", 5);
                    var c = configuration["Logging:LogLevel:Default"];
                    var d = configuration["ASPNETCORE_URLS"];
                    var e = configuration["Need:Me"] ?? throw new System.Exception();
                    var f = configuration["Both:K"] ?? "d"; var g = configuration["Both:K"];
                    var section = new Dictionary<string,string>(); var h = section["not-config"];
                }
            }
            """);
        var keys = new RoslynConfigKeySource().Read(Path.Combine(_dir, "orders")).Used.Select(u => u.Key).ToList();
        Assert.Equivalent(new[] { "Need:Me", "Both:K" }, keys);
    }

    [Fact]
    public void Ambiguous_service_node_is_not_guessed()
    {
        W("orders/docker-compose.yml", "services:\n  api:\n    image: a\n    environment: {Jwt__Secret: x}\n  worker:\n    image: b\n");
        W("orders/src/S.cs", "class S(Microsoft.Extensions.Configuration.IConfiguration configuration){ void R(){ var a=configuration[\"Jwt:Secret\"]; } }");
        var handler = new GetSystemHandler(new ComposeSystemSource(new Discovery(_dir)),
            new ServiceAnalyzer(new Discovery(_dir), new NoTypes(), new TypeClassifier()), configKeys: new RoslynConfigKeySource());
        var dto = handler.Handle(new GetSystemQuery(_dir, Layers: true))!;
        Assert.DoesNotContain(dto.Problems ?? [], x => x.Kind == "missing-env-key");   // два узла, имя не совпало — не склеиваем env обоих
    }

    [Fact]
    public void Appsettings_files_are_capped()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "orders"));
        for (var i = 0; i < 120; i++) W($"orders/cfg/{i}/appsettings.json", $$"""{ "K{{i}}": 1 }""");
        var declared = new RoslynConfigKeySource().Read(Path.Combine(_dir, "orders")).DeclaredByEnv;
        Assert.InRange(declared.Values.Sum(v => v.Count), 1, 50);
    }
}
