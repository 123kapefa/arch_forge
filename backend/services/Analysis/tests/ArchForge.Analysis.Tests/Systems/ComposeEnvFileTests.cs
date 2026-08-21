using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// G4 — переменные окружения из файлов: env_file (G4a) и .env для ${VAR} (G4b).
// Раньше читался только inline environment, а ${VAR} без дефолта уходил в «отложено» → рёбра терялись.
public class ComposeEnvFileTests
{
    private static SystemGraph Read(params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_g4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, content) in files) File.WriteAllText(Path.Combine(dir, name), content);
            return new ComposeSystemSource().Read(Path.Combine(dir, "docker-compose.yml"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static bool HasRwDb(SystemGraph g, string from, string to) =>
        g.Nodes.FirstOrDefault(n => n.Name == from) is { } s &&
        g.Nodes.FirstOrDefault(n => n.Name == to) is { } t &&
        g.Edges.Any(e => e.Source == s.Id && e.Target == t.Id && e.Kind == "rw-db");

    [Fact]
    public void EnvFile_connection_string_creates_edge()  // G4a
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    env_file:
                      - api.env
                  orders-db:
                    image: postgres:16
                """),
            ("api.env", "ConnectionStrings__Orders=Host=orders-db;Database=orders\n"));

        Assert.True(HasRwDb(g, "orders-api", "orders-db")); // строка подключения из env_file → ребро
    }

    [Fact]
    public void DotEnv_interpolates_environment_variable()  // G4b
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    environment:
                      ConnectionStrings__Orders: "${ORDERS_CONN}"
                  orders-db:
                    image: postgres:16
                """),
            (".env", "ORDERS_CONN=Host=orders-db;Database=orders\n"));

        Assert.True(HasRwDb(g, "orders-api", "orders-db")); // ${ORDERS_CONN} резолвнулся из .env → ребро
    }

    [Fact]
    public void Inline_environment_overrides_env_file()  // приоритет compose
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    env_file:
                      - api.env
                    environment:
                      ConnectionStrings__Orders: "Host=orders-db;Database=orders"
                  orders-db:
                    image: postgres:16
                """),
            ("api.env", "ConnectionStrings__Orders=Host=wrong-db\n")); // должен быть перекрыт inline

        Assert.True(HasRwDb(g, "orders-api", "orders-db"));                       // inline победил → ребро к orders-db
        Assert.DoesNotContain(g.Nodes, n => n.Name == "wrong-db");               // значение из env_file не применилось
    }

    [Fact]
    public void DotEnv_comments_quotes_and_export_parsed()  // формат .env
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    environment:
                      ConnectionStrings__Orders: "${ORDERS_CONN}"
                  orders-db:
                    image: postgres:16
                """),
            (".env", "# комментарий\nexport ORDERS_CONN=\"Host=orders-db;Database=orders\"\n\n"));

        Assert.True(HasRwDb(g, "orders-api", "orders-db")); // export + кавычки + комментарий разобраны
    }

    [Fact]
    public void EnvFile_as_string_form()  // env_file: path (строка, не список)
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    env_file: api.env
                  orders-db:
                    image: postgres:16
                """),
            ("api.env", "ConnectionStrings__Orders=Host=orders-db\n"));
        Assert.True(HasRwDb(g, "orders-api", "orders-db"));
    }

    [Fact]
    public void EnvFile_object_form_with_path()  // env_file: [{path: …}]
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    env_file:
                      - path: api.env
                  orders-db:
                    image: postgres:16
                """),
            ("api.env", "ConnectionStrings__Orders=Host=orders-db\n"));
        Assert.True(HasRwDb(g, "orders-api", "orders-db"));
    }

    [Fact]
    public void Multiple_env_files_later_wins()  // список env_file: последний перекрывает
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    env_file:
                      - a.env
                      - b.env
                  orders-db:
                    image: postgres:16
                """),
            ("a.env", "ConnectionStrings__Orders=Host=wrong-db\n"),
            ("b.env", "ConnectionStrings__Orders=Host=orders-db\n")); // позже → побеждает
        Assert.True(HasRwDb(g, "orders-api", "orders-db"));
        Assert.DoesNotContain(g.Nodes, n => n.Name == "wrong-db");
    }

    [Fact]
    public void DotEnv_overrides_default()  // .env перекрывает ${VAR:-default}
    {
        var g = Read(
            ("docker-compose.yml", """
                services:
                  orders-api:
                    image: app-api
                    environment:
                      ConnectionStrings__Orders: "${ORDERS_CONN:-Host=default-db}"
                  orders-db:
                    image: postgres:16
                """),
            (".env", "ORDERS_CONN=Host=orders-db\n")); // .env приоритетнее дефолта
        Assert.True(HasRwDb(g, "orders-api", "orders-db"));
        Assert.DoesNotContain(g.Nodes, n => n.Name == "default-db");
    }

    [Fact]
    public void Missing_env_files_do_not_crash()  // робастность
    {
        var g = Read(("docker-compose.yml", """
            services:
              orders-api:
                image: app-api
                env_file:
                  - nope.env
                environment:
                  ConnectionStrings__Orders: "${UNSET_VAR}"
              orders-db:
                image: postgres:16
            """));

        Assert.Contains(g.Nodes, n => n.Name == "orders-api");   // разбор не упал
        Assert.False(HasRwDb(g, "orders-api", "orders-db"));     // ничего не резолвнулось — ребра нет (честно)
    }
}
