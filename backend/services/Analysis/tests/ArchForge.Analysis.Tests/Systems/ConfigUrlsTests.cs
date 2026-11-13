using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

// E3.1 срез 3б: URL конфигурации сервисов по ключу (environment compose и appsettings*.json) — для клиентов с адресом из конфига.
public class ConfigUrlsTests
{
    [Theory]
    [InlineData("Payments__Url", "payments:url")]
    [InlineData("PAYMENTS__URL", "payments:url")]
    [InlineData("Services:Catalog:Url", "services:catalog:url")]
    public void Keys_are_normalized_like_dotnet_configuration(string key, string expected) =>
        Assert.Equal(expected, ConfigKeys.Normalize(key));

    [Theory]
    [InlineData("http://user:pw@catalog:8080/api/?token=1#x", "http://catalog:8080/api/")]
    [InlineData("https://catalog/", "https://catalog/")]
    [InlineData("ftp://catalog/", null)]
    [InlineData("${CATALOG_URL}", null)]
    [InlineData("не адрес", null)]
    public void Urls_are_cleaned_of_userinfo_and_query(string raw, string? expected) =>
        Assert.Equal(expected, ConfigKeys.CleanUrl(raw));

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archforge_cfgurls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Compose_environment_and_appsettings_urls_are_collected_per_service_without_secrets()
    {
        var dir = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "orders"));
            File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
                services:
                  orders:
                    image: app-orders
                    environment:
                      PaymentsService__Url: http://payments-api:8080
                      Smtp__Password: super-secret
                  payments-api:
                    image: app-payments
                """);
            File.WriteAllText(Path.Combine(dir, "orders", "appsettings.json"),
                """{ "Services": { "Catalog": { "Url": "http://catalog:9000/api?key=SECRET" } }, "Note": "plain text" }""");

            var g = new ComposeSystemSource().Read(dir);

            var orders = g.Nodes.Single(n => n.Name == "orders").Id;
            Assert.Contains(g.ConfigUrls!, c => c.Source == orders && c.Key == "paymentsservice:url" && c.Url == "http://payments-api:8080/" && c.FromEnvironment);
            Assert.Contains(g.ConfigUrls!, c => c.Source == orders && c.Key == "services:catalog:url" && c.Url == "http://catalog:9000/api" && !c.FromEnvironment);
            Assert.DoesNotContain(g.ConfigUrls!, c => c.Url.Contains("SECRET") || c.Key.Contains("smtp"));   // не-URL значения и query не попадают
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Env_url_key_with_deferred_value_is_recorded_as_unknown()
    {
        var dir = Temp();
        try
        {
            File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
                services:
                  orders:
                    image: app-orders
                    environment:
                      Payments__Url: ${PAYMENTS_URL}
                  payments:
                    image: app-payments
                """);
            var g = new ComposeSystemSource().Read(dir);
            var e = Assert.Single(g.ConfigUrls!, c => c.Key == "payments:url");
            Assert.True(e.FromEnvironment);
            Assert.Equal("", e.Url);          // ключ есть, значение неизвестно — перекрывает appsettings, но не даёт адреса
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
