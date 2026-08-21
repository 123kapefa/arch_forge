using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

public class ConnectionStringTests
{
    [Theory]
    [InlineData("Host=postgres-auth;Port=5432;Database=auth_db", "postgres-auth")]
    [InlineData("Server=db;Database=x", "db")]
    [InlineData("redis-auth:6379,password=secret", "redis-auth")]
    [InlineData("${CATALOG_DB_CONNECTION}", "${CATALOG_DB_CONNECTION}")]
    // URL-форма (G6): userinfo (логин:пароль) отрезается, остаётся только host
    [InlineData("amqp://guest:guest@rabbitmq:5672", "rabbitmq")]
    [InlineData("redis://cache:6379", "cache")]
    [InlineData("mongodb://mongo:27017/appdb", "mongo")]
    [InlineData("postgres://user:pw@pg-host:5432/app", "pg-host")]
    [InlineData("amqp://user:pa/ss@host:5672", "host")]                        // '/' в пароле (base64)
    [InlineData("amqp://user:p@ss@rabbitmq:5672", "rabbitmq")]                 // '@' в пароле
    [InlineData("amqp://user:pass@${RABBITMQ_HOST}:5672", "${RABBITMQ_HOST}")] // host отложен → Deferred (как Host=)
    public void ExtractHost_reads_host(string conn, string expected) =>
        Assert.Equal(expected, ConnectionString.ExtractHost(conn));

    [Theory]
    [InlineData("Database=x;Port=1")]                       // нет host-ключа
    [InlineData("amqp://user:pass@127.0.0.1:5672")]         // IP-литерал → не сервис
    [InlineData("amqp://user:pass@localhost:5672")]         // localhost → не сервис
    [InlineData("amqp://user:pass@[::1]:5672")]             // IPv6-литерал → не сервис
    [InlineData("amqp://LOCALHOST:5672")]                   // localhost UPPERCASE → не сервис
    public void ExtractHost_null_cases(string conn) =>
        Assert.Null(ConnectionString.ExtractHost(conn));

    [Fact]
    public void ExtractHost_never_leaks_password()  // secrets-config
    {
        var host = ConnectionString.ExtractHost("amqp://admin:sup3r-s3cret@rabbitmq:5672");
        Assert.Equal("rabbitmq", host);
        Assert.DoesNotContain("sup3r-s3cret", host!, StringComparison.Ordinal); // пароль не в результате
    }
}
