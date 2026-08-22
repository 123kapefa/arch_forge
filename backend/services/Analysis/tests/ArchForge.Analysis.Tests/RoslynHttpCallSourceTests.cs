using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Синтаксический сканер исходящих HTTP-вызовов (E3.1 срез 1): абсолютные URL-литералы у HttpClient.*Async и SendAsync.
public class RoslynHttpCallSourceTests
{
    private static IReadOnlyList<Core.HttpCallHit> Scan(string code)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "af_http_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Client.cs"), code);
            return new RoslynHttpCallSource().Read(tmp);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Detects_client_verbs_with_absolute_url()
    {
        var hits = Scan("""
            class C {
                async Task M(System.Net.Http.HttpClient http) {
                    await http.GetAsync("http://catalog-service/api/items");
                    await http.PostAsync("https://orders-service:8080/api/orders", null);
                    await http.DeleteAsync("http://orders-service/api/orders/42");
                }
            }
            """);

        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://catalog-service/api/items");
        // порт сохранён в Url (authority), но host для резолва вытащит потребитель
        Assert.Contains(hits, h => h.Method == "POST" && h.Url == "https://orders-service:8080/api/orders");
        Assert.Contains(hits, h => h.Method == "DELETE" && h.Url == "http://orders-service/api/orders/42");
        Assert.All(hits, h => Assert.EndsWith("Client.cs", h.File));
        Assert.All(hits, h => Assert.True(h.Line > 0));
    }

    [Fact]
    public void Detects_inline_send_async_short_and_fully_qualified()
    {
        var hits = Scan("""
            using System.Net.Http;
            class C {
                async Task M(HttpClient http) {
                    await http.SendAsync(new HttpRequestMessage(HttpMethod.Put, "http://progress-service/api/lessons/1"));
                    await http.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "http://access-service/api/grants"));
                }
            }
            """);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Method == "PUT" && h.Url == "http://progress-service/api/lessons/1");
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://access-service/api/grants");
    }

    [Fact]
    public void Variable_bound_request_message_is_not_detected()  // срез 1 без data-flow — известное ограничение
    {
        var hits = Scan("""
            using System.Net.Http;
            class C {
                async Task M(HttpClient http) {
                    var req = new HttpRequestMessage(HttpMethod.Put, "http://progress-service/api/lessons/1");
                    await http.SendAsync(req);   // адрес пришёл через переменную → нужен data-flow (позже)
                }
            }
            """);

        Assert.Empty(hits);
    }

    [Fact]
    public void Strips_query_string_from_url_and_evidence()  // secrets-config: не тащим query (может нести токены)
    {
        var hits = Scan("""
            class C {
                async Task M(System.Net.Http.HttpClient http) =>
                    await http.GetAsync("http://catalog-service/api/items?apiKey=s3cret&page=2");
            }
            """);

        var hit = Assert.Single(hits);
        Assert.Equal("http://catalog-service/api/items", hit.Url);
        Assert.DoesNotContain("s3cret", hit.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", hit.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Strips_query_string_in_send_async_branch_too()  // secrets-config: обе ветки идут через общий Add()
    {
        var hits = Scan("""
            using System.Net.Http;
            class C {
                async Task M(HttpClient http) =>
                    await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://auth-service/token?client_secret=s3cret"));
            }
            """);

        var hit = Assert.Single(hits);
        Assert.Equal("http://auth-service/token", hit.Url);
        Assert.DoesNotContain("s3cret", hit.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignores_relative_and_non_literal_urls()  // срез 1 — только абсолютные литералы
    {
        var hits = Scan("""
            class C {
                async Task M(System.Net.Http.HttpClient http, string url) {
                    await http.GetAsync("/api/items");         // относительный → срез 2
                    await http.GetAsync(url);                   // переменная → без семантики не резолвим
                    await http.PostAsync($"http://{url}/x", null); // интерполяция → не литерал
                }
            }
            """);

        Assert.Empty(hits);
    }

    [Fact]
    public void Ignores_non_http_schemes()  // amqp/ws и пр. — не HTTP-вызовы
    {
        var hits = Scan("""
            class C {
                void M(System.Net.Http.HttpClient http) =>
                    http.GetAsync("amqp://rabbitmq:5672");
            }
            """);

        Assert.Empty(hits);
    }
}
