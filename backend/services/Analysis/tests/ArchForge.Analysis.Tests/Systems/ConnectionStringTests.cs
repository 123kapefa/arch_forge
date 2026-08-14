using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

public class ConnectionStringTests
{
    [Theory]
    [InlineData("Host=postgres-auth;Port=5432;Database=auth_db", "postgres-auth")]
    [InlineData("Server=db;Database=x", "db")]
    [InlineData("redis-auth:6379,password=secret", "redis-auth")]
    [InlineData("${CATALOG_DB_CONNECTION}", "${CATALOG_DB_CONNECTION}")]
    public void ExtractHost_reads_host(string conn, string expected) =>
        Assert.Equal(expected, ConnectionString.ExtractHost(conn));

    [Fact]
    public void ExtractHost_null_when_no_host() =>
        Assert.Null(ConnectionString.ExtractHost("Database=x;Port=1"));
}
