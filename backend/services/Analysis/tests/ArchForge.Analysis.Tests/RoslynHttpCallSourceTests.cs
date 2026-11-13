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

    private static IReadOnlyList<Core.HttpCallHit> ScanFiles(params (string Name, string Code)[] files)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "af_http_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var (name, code) in files) File.WriteAllText(Path.Combine(tmp, name), code);
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

    // ── срез 2: относительные адреса + BaseAddress ──

    [Fact]
    public void Relative_path_resolves_against_base_address_in_same_method()
    {
        var hits = Scan("""
            class C {
                async Task M() {
                    var http = new System.Net.Http.HttpClient();
                    http.BaseAddress = new Uri("http://catalog:8080/");
                    await http.GetAsync("/api/products");
                    await http.PostAsync("api/products/reserve", null);
                }
            }
            """);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://catalog:8080/api/products" && h.ViaBase);
        Assert.Contains(hits, h => h.Method == "POST" && h.Url == "http://catalog:8080/api/products/reserve" && h.ViaBase);
    }

    [Fact]
    public void Object_initializer_base_address_and_base_path_are_combined()
    {
        var hits = Scan("""
            class C {
                async Task M() {
                    var http = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://catalog/v1/") };
                    await http.GetAsync("items/all");      // относительный без «/» — дописывается к пути базы
                    await http.GetAsync("/health");       // со «/» — заменяет путь базы
                }
            }
            """);

        Assert.Contains(hits, h => h.Url == "http://catalog/v1/items/all");
        Assert.Contains(hits, h => h.Url == "http://catalog/health");
    }

    [Fact]
    public void Base_address_from_add_http_client_lambda_is_found_at_class_level()
    {
        var hits = Scan("""
            class C {
                void Configure(IServiceCollection s) =>
                    s.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog:8080"));
                async Task Use(IHttpClientFactory f) {
                    var c = f.CreateClient("catalog");
                    await c.GetFromJsonAsync<object>("/api/products/5");
                }
            }
            """);

        var h = Assert.Single(hits);
        Assert.Equal("http://catalog:8080/api/products/5", h.Url);
        Assert.True(h.ViaBase);
    }

    [Fact]
    public void Two_different_base_addresses_in_scope_are_not_guessed()
    {
        var hits = Scan("""
            class C {
                async Task M(System.Net.Http.HttpClient a, System.Net.Http.HttpClient b) {
                    a.BaseAddress = new Uri("http://catalog/");
                    b.BaseAddress = new Uri("http://orders/");
                    await a.GetAsync("/api/x");
                }
            }
            """);

        Assert.Empty(hits);   // неоднозначно → не приписываем (adr/0008)
    }

    [Fact]
    public void Nearest_scope_wins_method_over_class()
    {
        var hits = Scan("""
            class C {
                void A(System.Net.Http.HttpClient c) { c.BaseAddress = new Uri("http://catalog/"); }
                async Task B(System.Net.Http.HttpClient c) {
                    c.BaseAddress = new Uri("http://orders/");
                    await c.GetAsync("/api/orders");
                }
            }
            """);

        var h = Assert.Single(hits);
        Assert.Equal("http://orders/api/orders", h.Url);   // в методе B свой базовый адрес, он ближе, чем из A
    }

    [Fact]
    public void Relative_path_without_known_base_is_skipped()
    {
        Assert.Empty(Scan("""
            class C { async Task M(System.Net.Http.HttpClient h) { var u = "http://x/"; await h.GetAsync("/api/orders"); } }
            """));
    }

    [Fact]
    public void Interpolated_path_holes_become_placeholders_and_query_is_dropped()
    {
        var hits = Scan("""
            class C {
                async Task M(System.Net.Http.HttpClient http, int id, string q) {
                    http.BaseAddress = new Uri("http://catalog/");
                    await http.GetAsync($"/api/products/{id}?search={q}&token=SECRET");
                    await http.GetAsync($"http://catalog/api/items/{id}/stock");
                    await http.GetAsync($"{id}/api");        // начинается с подстановки — адрес неизвестен
                }
            }
            """);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Url == "http://catalog/api/products/{id}");
        Assert.Contains(hits, h => h.Url == "http://catalog/api/items/{id}/stock");
        Assert.DoesNotContain(hits, h => h.Url.Contains("SECRET") || h.Evidence.Contains("SECRET"));
    }

    [Fact]
    public void Absolute_call_is_not_marked_as_via_base()
    {
        var hits = Scan("""class C { async Task M(System.Net.Http.HttpClient h) { await h.GetAsync("http://catalog/api/x"); } }""");
        Assert.False(Assert.Single(hits).ViaBase);
    }

    [Fact]
    public void Base_address_must_be_http_literal()
    {
        Assert.Empty(Scan("""
            class C { async Task M(System.Net.Http.HttpClient h, string cfg) {
                h.BaseAddress = new Uri(cfg);                  // не литерал — не знаем
                h.BaseAddress = new Uri("ftp://host/");        // не http(s)
                await h.GetAsync("/api/x"); } }
            """));
    }

    [Fact]
    public void Base_address_of_another_class_in_same_file_is_not_used()
    {
        Assert.Empty(Scan("""
            class Config { void C(System.Net.Http.HttpClient c) { c.BaseAddress = new Uri("http://catalog/"); } }
            class Other { async Task M(System.Net.Http.HttpClient h) { await h.GetAsync("/api/x"); } }
            """));
    }

    [Fact]
    public void Top_level_statements_resolve_against_file_base_address()
    {
        var hits = Scan("""
            var c = new System.Net.Http.HttpClient();
            c.BaseAddress = new Uri("http://catalog:8080/");
            await c.GetAsync("/api/ping");
            """);
        Assert.Equal("http://catalog:8080/api/ping", Assert.Single(hits).Url);
    }

    // ── замечания ревью среза 2 ──

    [Fact]
    public void Non_url_strings_are_not_expanded_by_base_address()
    {
        // ключи кэша, имена файлов, id, «схема:значение», протокол-относительные и обратные слэши — не HTTP-пути
        Assert.Empty(Scan("""
            class C { async Task M(System.Net.Http.HttpClient http, ICache cache, IRepo repo) {
                http.BaseAddress = new Uri("http://catalog/");
                await cache.GetStringAsync("user-list");
                await repo.GetAsync("id");
                await cache.GetStringAsync("user:42");
                await cache.GetStringAsync("tenant:7/items");
                await http.GetAsync("//evil-host/api/x");
                await http.GetAsync("\\\\server\\share");
            } }
            """));
    }

    [Fact]
    public void Only_first_argument_is_the_address()
    {
        // PostAsJsonAsync(url, "текст"): первый аргумент не литерал — адреса нет, а строка тела маршрутом не становится
        Assert.Empty(Scan("""
            class C { async Task M(System.Net.Http.HttpClient http, string url) {
                http.BaseAddress = new Uri("http://catalog/");
                await http.PostAsJsonAsync(url, "api/body/text");
            } }
            """));
    }

    [Fact]
    public void Top_level_call_ignores_base_address_of_a_class_in_same_file()
    {
        Assert.Empty(Scan("""
            await new System.Net.Http.HttpClient().GetAsync("/api/ping");
            class Helper { void C(System.Net.Http.HttpClient c) { c.BaseAddress = new Uri("http://catalog/"); } }
            """));
    }

    [Fact]
    public void Nested_class_does_not_inherit_sibling_nested_base_address()
    {
        Assert.Empty(Scan("""
            class Outer {
                class A { void C(System.Net.Http.HttpClient c) { c.BaseAddress = new Uri("http://catalog/"); } }
                class B { async Task M(System.Net.Http.HttpClient h) { await h.GetAsync("/api/x"); } }
            }
            """));
    }

    [Fact]
    public void Base_path_without_trailing_slash_is_replaced_like_http_client_does()
    {
        var hits = Scan("""
            class C { async Task M(System.Net.Http.HttpClient h) {
                h.BaseAddress = new Uri("http://catalog/v1");
                await h.GetAsync("api/x");
            } }
            """);
        Assert.Equal("http://catalog/api/x", Assert.Single(hits).Url);   // как у HttpClient: «v1» без слэша — не каталог
    }

    [Fact]
    public void Non_literal_base_address_and_bare_property_assignment_are_ignored()
    {
        Assert.Empty(Scan("""
            class C {
                System.Uri BaseAddress; string cfg = "http://x/";
                void Init() { BaseAddress = new Uri("http://catalog/"); }       // свой член класса, не клиент
                async Task M(System.Net.Http.HttpClient h, string c) { h.BaseAddress = new Uri(c); await h.GetAsync("/api/x"); }
            }
            """));
    }

    [Fact]
    public void Interpolated_absolute_url_is_recorded_without_query_and_secrets()
    {
        var hits = Scan("""class C { async Task M(System.Net.Http.HttpClient h, int id, string key) { await h.GetAsync($"http://catalog/a/{id}?key={key}"); } }""");
        var hit = Assert.Single(hits);
        Assert.Equal("http://catalog/a/{id}", hit.Url);
        Assert.False(hit.ViaBase);
    }

    [Fact]
    public void Via_base_evidence_says_so()
    {
        var hit = Assert.Single(Scan("""class C { async Task M(System.Net.Http.HttpClient h) { h.BaseAddress = new Uri("http://catalog/"); await h.GetAsync("/api/x"); } }"""));
        Assert.EndsWith("(по BaseAddress)", hit.Evidence);
    }

    // ── замечания security-ревью среза 2 ──

    [Fact]
    public void Encoded_question_mark_stays_encoded_and_control_characters_do_not_reach_evidence()
    {
        var hits = Scan("""
            class C { async Task M(System.Net.Http.HttpClient c) {
                c.BaseAddress = new Uri("http://orders:8080/api/");
                await c.GetAsync("/x%3Ftoken=1");
                await c.GetAsync("http://orders/y%3Ftoken=1");
                await c.GetAsync("/z%0Aline%00nul");
                await c.GetAsync("/real?token=SECRET");
            } }
            """);
        Assert.Equal(4, hits.Count);
        Assert.All(hits, h =>
        {
            var text = h.Url + h.Evidence;
            Assert.DoesNotContain("SECRET", text);          // настоящий query отброшен
            Assert.DoesNotContain('?', text);               // %3F не раскодируется в «?» и не прячет query
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('\0', text);             // управляющие символы остаются в экранированном виде
        });
        Assert.Contains(hits, h => h.Url == "http://orders:8080/x%3Ftoken=1");
    }

    [Fact]
    public void Scheme_like_strings_and_unc_paths_never_become_calls_even_with_base()
    {
        Assert.Empty(Scan("""
            class C { async Task M(System.Net.Http.HttpClient c) {
                c.BaseAddress = new Uri("https://api.telegram.org/");
                await c.GetAsync("bot123456:AAHsecretTOKEN/sendMessage");
                await c.GetAsync("javascript:alert(1)");
                await c.GetAsync("mailto:a@b");
                await c.GetAsync("x:/y");
                await c.GetAsync(@"\\unc-host\share\x");
            } }
            """));
    }

    [Fact]
    public void File_with_thousands_of_base_addresses_is_bounded()
    {
        var sb = new System.Text.StringBuilder("class C { async Task M(System.Net.Http.HttpClient c) {\n");
        for (var i = 0; i < 5000; i++) sb.Append($"c.BaseAddress = new Uri(\"http://h{i}/\");\n");
        for (var i = 0; i < 5000; i++) sb.Append("await c.GetAsync(\"/x/y\");\n");
        sb.Append("} }");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = Scan(sb.ToString());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"разбор занял {sw.Elapsed}");   // раньше — десятки секунд
        Assert.Empty(hits);   // сверх лимита адресов относительные пути не разрешаются
    }

    // ── срез 3а: типизированные и именованные клиенты, адрес в другом файле ──

    [Fact]
    public void Typed_client_base_address_from_another_file_applies_to_calls_in_client_class()
    {
        var hits = ScanFiles(
            ("Registration.cs", """
                class R { void Add(IServiceCollection s) =>
                    s.AddHttpClient<ICatalogClient, CatalogClient>(c => c.BaseAddress = new Uri("http://catalog:8080/")); }
                """),
            ("CatalogClient.cs", """
                class CatalogClient : ICatalogClient {
                    private readonly System.Net.Http.HttpClient _http;
                    public CatalogClient(System.Net.Http.HttpClient http) { _http = http; }
                    public Task<string> Get(int id) => _http.GetStringAsync($"/api/products/{id}");
                    public Task Reserve() => _http.PostAsync("api/products/reserve", null);
                }
                """));

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://catalog:8080/api/products/{id}" && h.ViaBase);
        Assert.Contains(hits, h => h.Method == "POST" && h.Url == "http://catalog:8080/api/products/reserve");
        Assert.All(hits, h => Assert.EndsWith("CatalogClient.cs", h.File));
    }

    [Fact]
    public void Single_type_argument_and_configure_http_client_chain_are_understood()
    {
        var hits = ScanFiles(
            ("Program.cs", """
                var b = WebApplication.CreateBuilder(args);
                b.Services.AddHttpClient<OrdersClient>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://orders/"));
                """),
            ("OrdersClient.cs", """
                class OrdersClient(System.Net.Http.HttpClient http) {
                    public Task Create() => http.PostAsync("/api/orders", null);
                }
                """));

        Assert.Equal("http://orders/api/orders", Assert.Single(hits).Url);
    }

    [Fact]
    public void Named_client_resolves_through_create_client_variable_and_chain()
    {
        var hits = ScanFiles(
            ("Program.cs", """
                class P { void Add(IServiceCollection s) =>
                    s.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog:8080")); }
                """),
            ("Use.cs", """
                class U {
                    async Task A(IHttpClientFactory f) {
                        var c = f.CreateClient("catalog");
                        await c.GetAsync("/api/products");
                    }
                    async Task B(IHttpClientFactory f) => await f.CreateClient("catalog").DeleteAsync("/api/products/1");
                }
                """));

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://catalog:8080/api/products");
        Assert.Contains(hits, h => h.Method == "DELETE" && h.Url == "http://catalog:8080/api/products/1");
    }

    [Fact]
    public void Conflicting_registrations_for_same_client_are_not_guessed()
    {
        Assert.Empty(ScanFiles(
            ("A.cs", """class A { void M(IServiceCollection s) => s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog/")); }"""),
            ("B.cs", """class B { void M(IServiceCollection s) => s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://other/")); }"""),
            ("CatalogClient.cs", """class CatalogClient { Task M(System.Net.Http.HttpClient h) => h.GetAsync("/api/x"); }""")));
    }

    [Fact]
    public void Typed_registration_without_literal_address_gives_nothing()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s, string u) => s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri(u)); }"""),
            ("CatalogClient.cs", """class CatalogClient { Task M(System.Net.Http.HttpClient h) => h.GetAsync("/api/x"); }""")));
    }

    [Fact]
    public void Class_that_is_not_a_registered_client_does_not_inherit_the_address()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog/")); }"""),
            ("Other.cs", """class OtherService { Task M(System.Net.Http.HttpClient h) => h.GetAsync("/api/x"); }""")));
    }

    [Fact]
    public void Named_client_with_unknown_name_or_unrelated_variable_gives_nothing()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog/")); }"""),
            ("Use.cs", """
                class U { async Task M(IHttpClientFactory f, System.Net.Http.HttpClient other) {
                    var x = f.CreateClient("billing");
                    await x.GetAsync("/api/a");
                    await other.GetAsync("/api/b");
                } }
                """)));
    }

    [Fact]
    public void More_than_the_registration_limit_disables_the_registry_but_a_few_registrations_work()
    {
        static (string, string)[] Files(int n)
        {
            var files = new List<(string, string)>();
            for (var i = 0; i < n; i++)
                files.Add(($"R{i}.cs", $"class R{i} {{ void M(IServiceCollection s) => s.AddHttpClient<C{i}>(c => c.BaseAddress = new Uri(\"http://h{i}/\")); }}"));
            files.Add(("C1.cs", "class C1(System.Net.Http.HttpClient h) { Task M() => h.GetAsync(\"/api/x\"); }"));
            return files.ToArray();
        }
        Assert.Equal("http://h1/api/x", Assert.Single(ScanFiles(Files(10))).Url);      // в пределах лимита — работает
        Assert.Empty(ScanFiles(Files(600)));                                            // сверх лимита 512 — реестр не используем
    }

    // ── замечания ревью среза 3а ──

    [Fact]
    public void Two_different_base_addresses_inside_registered_client_class_are_not_overridden_by_registry()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://registry/")); }"""),
            ("CatalogClient.cs", """
                class CatalogClient {
                    Task M(System.Net.Http.HttpClient a, System.Net.Http.HttpClient b) {
                        a.BaseAddress = new Uri("http://one/");
                        b.BaseAddress = new Uri("http://two/");
                        return a.GetAsync("/api/x");
                    }
                }
                """)));   // неоднозначность в области реестром не «лечится»
    }

    [Fact]
    public void Address_in_scope_has_priority_over_registry()
    {
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://registry/")); }"""),
            ("CatalogClient.cs", """class CatalogClient { Task M(System.Net.Http.HttpClient h) { h.BaseAddress = new Uri("http://local/"); return h.GetAsync("/api/x"); } }"""));
        Assert.Equal("http://local/api/x", Assert.Single(hits).Url);
    }

    [Fact]
    public void Explicit_create_client_inside_typed_client_class_uses_its_own_name()
    {
        var hits = ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s) {
                    s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog/"));
                    s.AddHttpClient("billing", c => c.BaseAddress = new Uri("http://billing/"));
                } }
                """),
            ("CatalogClient.cs", """
                class CatalogClient(System.Net.Http.HttpClient http, IHttpClientFactory f) {
                    Task A() => http.GetAsync("/api/own");
                    Task B() => f.CreateClient("billing").GetAsync("/api/pay");
                    Task C() => f.CreateClient("unknown").GetAsync("/api/nope");      // имя не зарегистрировано — не приписываем классу
                }
                """));
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Url == "http://catalog/api/own");
        Assert.Contains(hits, h => h.Url == "http://billing/api/pay");
    }

    [Fact]
    public void Nested_class_inside_typed_client_does_not_inherit_the_address()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog/")); }"""),
            ("CatalogClient.cs", """class CatalogClient { class Helper { Task M(System.Net.Http.HttpClient h) => h.GetAsync("/api/x"); } }""")));
    }

    [Fact]
    public void Only_implementation_of_typed_pair_gets_the_address_not_the_interface()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<ICatalogClient, CatalogClient>(c => c.BaseAddress = new Uri("http://catalog/")); }"""),
            ("I.cs", """class ICatalogClient { Task M(System.Net.Http.HttpClient h) => h.GetAsync("/api/x"); }""")));
    }

    [Fact]
    public void Named_client_registered_twice_with_different_addresses_and_variable_with_two_names_are_not_guessed()
    {
        Assert.Empty(ScanFiles(
            ("A.cs", """class A { void M(IServiceCollection s) => s.AddHttpClient("c", x => x.BaseAddress = new Uri("http://a/")); }"""),
            ("B.cs", """class B { void M(IServiceCollection s) => s.AddHttpClient("c", x => x.BaseAddress = new Uri("http://b/")); }"""),
            ("U.cs", """class U { Task M(IHttpClientFactory f) { var c = f.CreateClient("c"); return c.GetAsync("/api/x"); } }""")));
        Assert.Empty(ScanFiles(
            ("A.cs", """
                class A { void M(IServiceCollection s) {
                    s.AddHttpClient("one", x => x.BaseAddress = new Uri("http://one/"));
                    s.AddHttpClient("two", x => x.BaseAddress = new Uri("http://two/"));
                } }
                """),
            ("U.cs", """
                class U { Task M(IHttpClientFactory f) {
                    var c = f.CreateClient("one");
                    c = f.CreateClient("two");
                    return c.GetAsync("/api/x");
                } }
                """)));
    }

    [Fact]
    public void Record_with_primary_constructor_and_await_wrapped_create_client_work()
    {
        var hits = ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s) {
                    s.AddHttpClient<PriceClient>(c => c.BaseAddress = new Uri("http://prices/"));
                    s.AddHttpClient("stock", c => c.BaseAddress = new Uri("http://stock/"));
                } }
                """),
            ("PriceClient.cs", """record PriceClient(System.Net.Http.HttpClient Http) { Task<string> Get() => Http.GetStringAsync("/api/prices"); }"""),
            ("Use.cs", """class U { async Task M(IHttpClientFactory f) { var c = await Task.FromResult(f.CreateClient("stock")); await c.GetAsync("/api/stock"); } }"""));
        Assert.Contains(hits, h => h.Url == "http://prices/api/prices");
    }

    [Fact]
    public void Chained_second_add_http_client_does_not_lend_its_lambda_to_the_first_registration()
    {
        // AddHttpClient<A>() без адреса, затем отдельная регистрация B с адресом: A адреса не получает
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<A>().Services.AddHttpClient<B>(c => c.BaseAddress = new Uri("http://b/")); }"""),
            ("A.cs", """class A { Task M(System.Net.Http.HttpClient h) => h.GetAsync("/api/x"); }""")));
    }

    // ── замечания security-ревью среза 3а ──

    [Fact]
    public void Typed_address_goes_only_to_injected_http_client_not_to_new_or_foreign_clients()
    {
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<Foo>(c => c.BaseAddress = new Uri("http://victim/")); }"""),
            ("Foo.cs", """
                namespace Other;
                class Foo(System.Net.Http.HttpClient injected, IHttpClientFactory f) {
                    System.Net.Http.HttpClient own = new System.Net.Http.HttpClient();
                    async Task M(System.Net.Http.HttpClient param) {
                        await own.GetAsync("/own");                      // создан через new — не внедрённый
                        await param.GetAsync("/param");                   // параметр метода — не внедрённый
                        var o = f.CreateClient("other");
                        await o.GetAsync("/other");                       // из CreateClient — именованный, имя не зарегистрировано
                        await injected.GetAsync("/ok");                   // первичный конструктор — внедрённый
                    }
                }
                """));
        Assert.Equal("http://victim/ok", Assert.Single(hits).Url);
    }

    [Fact]
    public void Nested_registrations_in_one_file_are_linear()
    {
        // вложенные AddHttpClient-лямбды раньше обходились заново на каждой регистрации (квадрат)
        var sb = new System.Text.StringBuilder("class R { void M(IServiceCollection s) {\n");
        for (var i = 0; i < 300; i++) sb.Append($"s.AddHttpClient(\"n{i}\", c{i} => {{\n");
        for (var i = 0; i < 20000; i++) sb.Append("x.Foo(1, 2, 3);\n");
        for (var i = 0; i < 300; i++) sb.Append("});\n");
        sb.Append("} }");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = ScanFiles(("R.cs", sb.ToString()), ("U.cs", "class U { Task M(System.Net.Http.HttpClient h) => h.GetAsync(\"/x/y\"); }"));
        Assert.Empty(hits);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"разбор занял {sw.Elapsed}");
    }

    // ── срез 3б: адрес клиента из конфигурации ──

    [Fact]
    public void Options_pattern_resolves_section_from_const_and_emits_config_key()
    {
        var hits = ScanFiles(
            ("PaymentsServiceOptions.cs", """
                public sealed class PaymentsServiceOptions { public const string SectionName = "PaymentsService"; public string Url { get; set; } = ""; }
                """),
            ("Ext.cs", """
                class Ext { void Add(IServiceCollection services) =>
                    services.AddHttpClient<IPaymentsCommunicationService, PaymentsHttpClient>((sp, client) =>
                    {
                        var options = sp.GetRequiredService<IOptions<PaymentsServiceOptions>>().Value;
                        client.BaseAddress = new Uri(options.Url);
                    }); }
                """),
            ("PaymentsHttpClient.cs", """
                class PaymentsHttpClient(System.Net.Http.HttpClient httpClient) {
                    Task Refund() => httpClient.PostAsJsonAsync($"/api/payments/{id}/refund", null);
                }
                """));

        var h = Assert.Single(hits);
        Assert.Equal("PaymentsService:Url", h.ConfigKey);
        Assert.Equal("/api/payments/{id}/refund", h.Url);          // относительный путь; хоста у вызова ещё нет
        Assert.True(h.ViaBase);
        Assert.EndsWith("PaymentsHttpClient.cs", h.File);
    }

    [Fact]
    public void Options_type_without_const_uses_type_name_without_suffix()
    {
        var hits = ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s) => s.AddHttpClient<OrdersClient>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OrdersApiOptions>>().Value.BaseUrl)); }
                """),
            ("OrdersClient.cs", """class OrdersClient(System.Net.Http.HttpClient http) { Task M() => http.GetAsync("/api/orders"); }"""));
        Assert.Equal("OrdersApi:BaseUrl", Assert.Single(hits).ConfigKey);
    }

    [Fact]
    public void Direct_configuration_keys_are_understood()
    {
        var hits = ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s, IConfiguration configuration) {
                    s.AddHttpClient("a", c => c.BaseAddress = new Uri(configuration["Services:Catalog:Url"]!));
                    s.AddHttpClient("b", c => c.BaseAddress = new Uri(configuration.GetSection("Services")["Orders"] ?? "http://x"));
                    s.AddHttpClient("c", c => c.BaseAddress = new Uri(configuration.GetValue<string>("Services:Stock")!));
                    s.AddHttpClient("d", c => c.BaseAddress = new Uri(configuration.GetSection("Services:Billing").Value!));
                } }
                """),
            ("Use.cs", """
                class U { Task M(IHttpClientFactory f) { return Task.WhenAll(
                    f.CreateClient("a").GetAsync("/a"), f.CreateClient("b").GetAsync("/b"),
                    f.CreateClient("c").GetAsync("/c"), f.CreateClient("d").GetAsync("/d")); } }
                """));
        Assert.Equal(4, hits.Count);
        Assert.Contains(hits, h => h.Url == "/a" && h.ConfigKey == "Services:Catalog:Url");
        Assert.Contains(hits, h => h.Url == "/b" && h.ConfigKey == "Services:Orders");
        Assert.Contains(hits, h => h.Url == "/c" && h.ConfigKey == "Services:Stock");
        Assert.Contains(hits, h => h.Url == "/d" && h.ConfigKey == "Services:Billing");
    }

    [Fact]
    public void Non_configuration_expressions_give_no_key_and_no_call()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s, string any, Dictionary<string,string> d) {
                    s.AddHttpClient<A>(c => c.BaseAddress = new Uri(any));
                    s.AddHttpClient<B>(c => c.BaseAddress = new Uri(d["x"]));      // словарь — не конфигурация
                } }
                """),
            ("A.cs", """class A(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/a"); }"""),
            ("B.cs", """class B(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/b"); }""")));
    }

    [Fact]
    public void Conflicting_options_sections_for_same_class_name_are_not_guessed()
    {
        Assert.Empty(ScanFiles(
            ("O1.cs", """namespace A { class XOptions { public const string SectionName = "One"; public string Url {get;set;} } }"""),
            ("O2.cs", """namespace B { class XOptions { public const string SectionName = "Two"; public string Url {get;set;} } }"""),
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<C>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<XOptions>>().Value.Url)); }"""),
            ("C.cs", """class C(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/x"); }""")));
    }

    [Fact]
    public void Config_hits_never_carry_configuration_values_or_secrets()
    {
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s, IConfiguration configuration) => s.AddHttpClient<C>(c => c.BaseAddress = new Uri(configuration["Svc:Url"]!)); }"""),
            ("C.cs", """class C(System.Net.Http.HttpClient h) { Task M(string tok) => h.GetAsync($"/api/x?token={tok}"); }"""));
        var h = Assert.Single(hits);
        Assert.DoesNotContain("token", h.Url + h.Evidence);
        Assert.Equal("Svc:Url", h.ConfigKey);
    }

    // ── замечания ревью среза 3б ──

    [Fact]
    public void Nested_options_member_nameof_section_and_foreign_names_give_no_guessed_key()
    {
        Assert.Empty(ScanFiles(
            ("O.cs", """public class NamedOptions { public const string SectionName = nameof(NamedOptions); public string Url { get; set; } }"""),
            ("Reg.cs", """
                class R { void M(IServiceCollection s, Dictionary<string,string> appConfigMap) {
                    s.AddHttpClient<A>((sp, c) => { var o = sp.GetRequiredService<IOptions<PaymentsOptions>>().Value.Inner; c.BaseAddress = new Uri(o.Url); });
                    s.AddHttpClient<B>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<NamedOptions>>().Value.Url));
                    s.AddHttpClient<C>(c => c.BaseAddress = new Uri(appConfigMap["x"]));
                } }
                """),
            ("A.cs", """class A(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/a"); }"""),
            ("B.cs", """class B(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/b"); }"""),
            ("C.cs", """class C(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/c"); }""")));
    }

    [Fact]
    public void Section_inferred_from_class_name_is_marked_in_evidence_but_const_section_is_not()
    {
        var hits = ScanFiles(
            ("O.cs", """public class FooOptions { public string Url { get; set; } } public class BarOptions { public const string SectionName = "Bar"; public string Url { get; set; } }"""),
            ("Reg.cs", """
                class R { void M(IServiceCollection s) {
                    s.AddHttpClient<A>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<FooOptions>>().Value.Url));
                    s.AddHttpClient<B>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<BarOptions>>().Value.Url));
                } }
                """),
            ("A.cs", """class A(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/a"); }"""),
            ("B.cs", """class B(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/b"); }"""));
        Assert.Contains("секция выведена из имени класса", hits.Single(h => h.Url == "/a").Evidence);
        Assert.DoesNotContain("выведена", hits.Single(h => h.Url == "/b").Evidence);
        Assert.Equal("Bar:Url", hits.Single(h => h.Url == "/b").ConfigKey);
    }

    // ── замечания security-ревью среза 3б ──

    [Fact]
    public void Query_and_fragment_of_relative_literal_do_not_reach_config_hit()
    {
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s, IConfiguration configuration) => s.AddHttpClient<C>(c => c.BaseAddress = new Uri(configuration["Payments:Url"]!)); }"""),
            ("C.cs", """class C(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/api/pay?token=SUPERSECRET#frag"); }"""));
        var h = Assert.Single(hits);
        Assert.Equal("/api/pay", h.Url);
        Assert.DoesNotContain("SUPERSECRET", h.Url + h.Evidence);
        Assert.DoesNotContain("frag", h.Url + h.Evidence);
    }

    [Fact]
    public void Deeply_nested_lambdas_with_many_options_assignments_stay_fast()
    {
        var sb = new System.Text.StringBuilder("class R { void M() {\n");
        for (var i = 0; i < 600; i++) sb.Append("F(() => {\n");
        for (var i = 0; i < 64; i++) sb.Append($"var h{i} = new System.Net.Http.HttpClient {{ BaseAddress = new Uri(opt.Url{i}) }};\n");
        for (var i = 0; i < 600; i++) sb.Append("});\n");
        sb.Append("} }");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(Scan(sb.ToString()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"разбор занял {sw.Elapsed}");   // раньше — 13 с на 54 КБ
    }

    // ── срез 4: Refit ──

    [Fact]
    public void Refit_interface_methods_become_calls_with_base_from_configure_http_client()
    {
        var hits = ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s) =>
                    s.AddRefitClient<ICatalogApi>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://catalog:8080/")); }
                """),
            ("ICatalogApi.cs", """
                using Refit;
                public interface ICatalogApi {
                    [Get("/api/products/{id}")] Task<Product> Get(int id);
                    [Post("/api/products")] Task Create([Body] Product p);
                    [Delete("api/products/{id}?force=true")] Task Remove(int id);
                    [Headers("Accept: application/json")] Task NotACall();
                }
                """));

        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://catalog:8080/api/products/{id}" && h.ViaBase);
        Assert.Contains(hits, h => h.Method == "POST" && h.Url == "http://catalog:8080/api/products");
        Assert.Contains(hits, h => h.Method == "DELETE" && h.Url == "http://catalog:8080/api/products/{id}");   // query отброшен
        Assert.All(hits, h => { Assert.EndsWith("ICatalogApi.cs", h.File); Assert.Contains("Refit ICatalogApi.", h.Evidence); });
        Assert.DoesNotContain(hits, h => h.Evidence.Contains("force"));
    }

    [Fact]
    public void Rest_service_for_with_literal_address_is_a_registration()
    {
        var hits = ScanFiles(
            ("Use.cs", """class U { object M() => RestService.For<IOrdersApi>("http://orders:8080"); }"""),
            ("IOrdersApi.cs", """using Refit; interface IOrdersApi { [Get("/api/orders")] Task<string> List(); }"""));
        Assert.Equal("http://orders:8080/api/orders", Assert.Single(hits).Url);
    }

    [Fact]
    public void Refit_address_from_configuration_gives_config_key()
    {
        var hits = ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s) =>
                    s.AddRefitClient<IPaymentsApi>().ConfigureHttpClient((sp, c) =>
                        c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaymentsServiceOptions>>().Value.Url)); }
                """),
            ("O.cs", """class PaymentsServiceOptions { public const string SectionName = "PaymentsService"; public string Url { get; set; } }"""),
            ("I.cs", """using Refit; interface IPaymentsApi { [Post("/api/payments/{id}/refund")] Task Refund(string id); }"""));
        var h = Assert.Single(hits);
        Assert.Equal("PaymentsService:Url", h.ConfigKey);
        Assert.Equal("/api/payments/{id}/refund", h.Url);
    }

    [Fact]
    public void Unregistered_interface_ambiguous_addresses_and_no_address_give_nothing()
    {
        Assert.Empty(ScanFiles(
            ("I.cs", """using Refit; interface IUnregistered { [Get("/api/x")] Task X(); }"""),
            ("A.cs", """class A { void M(IServiceCollection s) => s.AddRefitClient<ITwo>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://one/")); }"""),
            ("B.cs", """class B { void M(IServiceCollection s) => s.AddRefitClient<ITwo>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://two/")); }"""),
            ("T.cs", """using Refit; interface ITwo { [Get("/api/two")] Task T(); }"""),
            ("C.cs", """class C { void M(IServiceCollection s) => s.AddRefitClient<INoBase>(); }"""),
            ("N.cs", """using Refit; interface INoBase { [Get("/api/nobase")] Task N(); }""")));
    }

    [Fact]
    public void Non_http_attributes_and_empty_or_hostile_paths_are_ignored()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddRefitClient<IApi>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://svc/")); }"""),
            ("I.cs", """
                using Refit;
                interface IApi {
                    [HttpGet("/api/not-refit")] Task A();       // ASP.NET-атрибут, не Refit
                    [Get("")] Task B();                          // пусто
                    [Get("//evil/x")] Task C();                  // протокол-относительный
                    [Get("ftp://svc/x")] Task D();               // чужая схема
                    [Get] Task E();                              // без пути
                    [Obsolete("/api/x")] Task F();               // другой атрибут
                }
                """)));
    }

    // ── замечания ревью среза 4 ──

    [Fact]
    public void Refit_appends_path_to_base_path_unlike_http_client()
    {
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddRefitClient<IApi>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://svc/v1/")); }"""),
            ("I.cs", """using Refit; interface IApi { [Get("/items")] Task A(); [Get("orders/{id}")] Task B(int id); }"""));
        Assert.Contains(hits, h => h.Url == "http://svc/v1/items");           // у HttpClient было бы http://svc/items
        Assert.Contains(hits, h => h.Url == "http://svc/v1/orders/{id}");
        Assert.All(hits, h => Assert.True(h.AppendToBasePath));
    }

    [Fact]
    public void Refit_interface_without_refit_word_in_file_is_found_via_verb_attributes()
    {
        // global using Refit: в файле интерфейса слова «Refit» нет
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddRefitClient<IApi>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://svc/")); }"""),
            ("I.cs", """public interface IApi { [Get("/api/x")] Task A(); }"""));
        Assert.Equal("http://svc/api/x", Assert.Single(hits).Url);
    }

    [Fact]
    public void Qualified_rest_service_client_variable_and_settings_lambda_forms_work()
    {
        var hits = ScanFiles(
            ("Use.cs", """
                class U {
                    object A() => Refit.RestService.For<IApiA>("http://a/");
                    object B() { var client = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://b/") }; return RestService.For<IApiB>(client); }
                    void C(IServiceCollection s) => s.AddRefitClient<IApiC>(sp => new RefitSettings()).ConfigureHttpClient(c => c.BaseAddress = new Uri("http://c/"));
                }
                """),
            ("I.cs", """using Refit; interface IApiA { [Get("/a")] Task A(); } interface IApiB { [Get("/b")] Task B(); } interface IApiC { [Get("/c")] Task C(); }"""));
        Assert.Contains(hits, h => h.Url == "http://a/a");
        Assert.Contains(hits, h => h.Url == "http://b/b");
        Assert.Contains(hits, h => h.Url == "http://c/c");
    }

    [Fact]
    public void Same_address_registered_twice_is_not_ambiguous_but_non_literal_for_is_ignored()
    {
        var hits = ScanFiles(
            ("A.cs", """class A { void M(IServiceCollection s) => s.AddRefitClient<IApi>().ConfigureHttpClient(c => c.BaseAddress = new Uri("http://svc/")); }"""),
            ("B.cs", """class B { object M() => RestService.For<IApi>("http://svc/"); }"""),
            ("I.cs", """using Refit; interface IApi { [Get("/x")] Task X(); }"""));
        Assert.Equal("http://svc/x", Assert.Single(hits).Url);
        Assert.Empty(ScanFiles(
            ("V.cs", """class V { object M(string url) => RestService.For<IOther>(url); }"""),
            ("I.cs", """using Refit; interface IOther { [Get("/x")] Task X(); }""")));
    }

    [Fact]
    public void Relative_path_with_url_in_query_is_still_a_path()
    {
        var hit = Assert.Single(Scan("""class C { async Task M(System.Net.Http.HttpClient h) { h.BaseAddress = new Uri("http://svc/"); await h.GetAsync("/redirect?to=http://other/x"); } }"""));
        Assert.Equal("http://svc/redirect", hit.Url);
    }

    // ── замечания security-ревью среза 4 ──

    [Fact]
    public void Same_named_refit_interfaces_in_different_namespaces_or_nested_are_not_guessed()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { object M() => RestService.For<IApi>("http://h/"); }"""),
            ("A.cs", """using Refit; namespace A { interface IApi { [Get("/a")] Task X(); } }"""),
            ("B.cs", """using Refit; namespace B { interface IApi { [Get("/b")] Task X(); } }""")));
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { object M() => RestService.For<IApi>("http://h/"); }"""),
            ("N.cs", """using Refit; class Outer { interface IApi { [Get("/c")] Task X(); } }""")));
    }

    [Fact]
    public void Control_characters_in_refit_path_are_rejected()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s, IConfiguration configuration) => s.AddRefitClient<IApi>().ConfigureHttpClient(c => c.BaseAddress = new Uri(configuration["Svc:Url"]!)); }"""),
            ("I.cs", "using Refit; interface IApi { [Get(\"/a\\n//evil\")] Task X(); }")));
    }

    [Fact]
    public void Many_rest_service_registrations_with_options_stay_fast()
    {
        var sb = new System.Text.StringBuilder("class R { void M(IServiceProvider sp) {\n var cfg = sp.GetRequiredService<IOptions<O>>().Value;\n");
        for (var i = 0; i < 510; i++) sb.Append($"RestService.For<I{i}>(cfg.P{i});\n");
        for (var i = 0; i < 20000; i++) sb.Append("x.Foo(1, 2, 3);\n");
        sb.Append("} }");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Scan(sb.ToString());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"разбор занял {sw.Elapsed}");   // раньше — секунды на каждую регистрацию
    }

    // ── секция options из регистрации (клиентские библиотеки) ──

    [Fact]
    public void Section_comes_from_configure_registration_with_nameof_not_from_class_name_guess()
    {
        var hits = ScanFiles(
            ("TagServiceOptions.cs", """public class TagServiceOptions { public string Url { get; set; } = ""; public int TimeoutSeconds { get; set; } }"""),
            ("Ext.cs", """
                public static class TagServiceExtensions {
                    public static IServiceCollection AddTagServiceHttpCommunication(this IServiceCollection services, IConfiguration configuration)
                    {
                        services.Configure<TagServiceOptions>(configuration.GetSection(nameof(TagServiceOptions)));
                        services.AddHttpClient<ITagServiceClient, TagServiceClient>((sp, config) =>
                        {
                            TagServiceOptions options = sp.GetRequiredService<IOptions<TagServiceOptions>>().Value;
                            config.BaseAddress = new Uri(options.Url);
                        });
                        return services;
                    }
                }
                """),
            ("TagServiceClient.cs", """public class TagServiceClient(System.Net.Http.HttpClient http) { Task M() => http.GetAsync("/api/tags"); }"""));
        var h = Assert.Single(hits);
        Assert.Equal("TagServiceOptions:Url", h.ConfigKey);       // секция = nameof(TagServiceOptions), а не «TagService» по имени класса
        Assert.DoesNotContain("выведена", h.Evidence);
    }

    [Theory]
    [InlineData("""services.Configure<XOptions>(configuration.GetSection("Services:X"));""", "Services:X:Url")]
    [InlineData("""services.AddOptions<XOptions>().Bind(configuration.GetSection("Svc"));""", "Svc:Url")]
    [InlineData("""services.AddOptions<XOptions>().BindConfiguration("Bound:Here");""", "Bound:Here:Url")]
    [InlineData("""services.Configure<XOptions>(configuration.GetSection(XOptions.SectionName));""", "Const:Url")]
    public void Section_registration_forms_are_understood(string registration, string key)
    {
        var hits = ScanFiles(
            ("O.cs", """public class XOptions { public const string SectionName = "Const"; public string Url { get; set; } }"""),
            ("Reg.cs", "class R { void M(IServiceCollection services, IConfiguration configuration) { " + registration
                + " services.AddHttpClient<C>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<XOptions>>().Value.Url)); } }"),
            ("C.cs", """class C(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/a"); }"""));
        Assert.Equal(key, Assert.Single(hits).ConfigKey);
    }

    [Fact]
    public void Same_options_registered_under_two_sections_is_ambiguous()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """
                class R { void M(IServiceCollection s, IConfiguration c) {
                    s.Configure<XOptions>(c.GetSection("One"));
                    s.Configure<XOptions>(c.GetSection("Two"));
                    s.AddHttpClient<C>((sp, cl) => cl.BaseAddress = new Uri(sp.GetRequiredService<IOptions<XOptions>>().Value.Url));
                } }
                """),
            ("C.cs", """class C(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/a"); }""")));
    }

    // ── обёртки базового клиента: `GetAsync<T>(url)` без получателя в классе типизированного клиента ──

    [Fact]
    public void Unqualified_wrapper_calls_in_registered_typed_client_are_http_calls()
    {
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<IFileClient, FileClient>(c => c.BaseAddress = new Uri("http://file-service:8002/")); }"""),
            ("FileClient.cs", """
                internal sealed class FileClient : BaseHttpClient, IFileClient {
                    public FileClient(System.Net.Http.HttpClient h) : base(h) { }
                    public Task A(Guid id, CancellationToken ct) => GetAsync<Video>($"/videos/{id}", ct);
                    public Task B(CancellationToken ct) => GetOptionalAsync<Video>("/videos/latest", ct);
                    public Task C(Body b, CancellationToken ct) => PostAsync<Body, Video>("/videos", b, ct);
                    public Task D(Guid id, CancellationToken ct) => this.DeleteAsync($"/videos/{id}", ct);
                    public Task E(string q, CancellationToken ct) => GetAsync<Video>($"/files/by-entity?entityId={q}", ct);
                }
                """));

        Assert.Equal(5, hits.Count);
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://file-service:8002/videos/{id}");
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://file-service:8002/videos/latest");
        Assert.Contains(hits, h => h.Method == "POST" && h.Url == "http://file-service:8002/videos");
        Assert.Contains(hits, h => h.Method == "DELETE" && h.Url == "http://file-service:8002/videos/{id}");
        Assert.Contains(hits, h => h.Method == "GET" && h.Url == "http://file-service:8002/files/by-entity");     // query отброшен
    }

    [Fact]
    public void Wrapper_calls_outside_registered_typed_clients_are_ignored()
    {
        Assert.Empty(ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<IFileClient, FileClient>(c => c.BaseAddress = new Uri("http://file-service/")); }"""),
            // репозиторий с похожим методом: класс не зарегистрирован как клиент
            ("Repo.cs", """class Repo : BaseRepo { Task A() => GetAsync<Item>("/items/1"); Task B() => DeleteAsync("/items/2"); }"""),
            // вложенный класс внутри клиента — свой, не клиент
            ("FileClient.cs", """class FileClient : BaseHttpClient { class Helper { Task A() => GetAsync<X>("/nested"); } }""")));
    }

    [Fact]
    public void Wrapper_with_config_address_gives_config_key()
    {
        var hits = ScanFiles(
            ("O.cs", """class FileServiceOptions { public string Url { get; set; } }"""),
            ("Reg.cs", """
                class R { void M(IServiceCollection s, IConfiguration configuration) {
                    s.Configure<FileServiceOptions>(configuration.GetSection(nameof(FileServiceOptions)));
                    s.AddHttpClient<IFileClient, FileClient>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<FileServiceOptions>>().Value.Url));
                } }
                """),
            ("FileClient.cs", """class FileClient(System.Net.Http.HttpClient h) : BaseHttpClient(h) { Task A(Guid id) => GetAsync<V>($"/videos/{id}"); }"""));
        var hit = Assert.Single(hits);
        Assert.Equal("FileServiceOptions:Url", hit.ConfigKey);
        Assert.Equal("/videos/{id}", hit.Url);
    }

    // ── замечания ревью: обёртки ──

    [Fact]
    public void Own_non_http_method_with_wrapper_name_is_not_a_call_but_own_http_wrapper_is()
    {
        var hits = ScanFiles(
            ("Reg.cs", """class R { void M(IServiceCollection s) => s.AddHttpClient<IClient, Client>(c => c.BaseAddress = new Uri("http://svc/")); }"""),
            ("Client.cs", """
                class Client(System.Net.Http.HttpClient http) : IClient {
                    // кэш-ключ, не HTTP: параметр в HttpClient не передаётся
                    private Task<string> GetAsync(string key) => Task.FromResult(key);
                    public Task A() => GetAsync("user/profile");
                    // собственная HTTP-обёртка: URL уходит в HttpClient
                    private Task<T> PostAsync<T>(string url, object body) => http.PostAsJsonAsync(url, body);
                    public Task B() => PostAsync<int>("/api/things", new { });
                }
                """));
        Assert.Equal("http://svc/api/things", Assert.Single(hits).Url);
    }

    [Fact]
    public void This_and_base_send_async_still_reach_the_normal_path()
    {
        var hits = ScanFiles(("Own.cs", """
            class C : Base {
                async Task M(System.Net.Http.HttpClient h) {
                    h.BaseAddress = new Uri("http://svc/");
                    await h.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/ping"));
                }
            }
            """));
        Assert.Equal("http://svc/ping", Assert.Single(hits).Url);
    }

    [Fact]
    public void Unresolvable_section_constant_does_not_erase_the_class_constant()
    {
        var hits = ScanFiles(
            ("O.cs", """class XOptions { public const string SectionName = "Real"; public string Url { get; set; } }"""),
            ("Reg.cs", """
                class R { void M(IServiceCollection s, IConfiguration c) {
                    s.Configure<XOptions>(c.GetSection(Missing.Name));    // константа не читается — регистрацию не учитываем
                    s.AddHttpClient<C>((sp, cl) => cl.BaseAddress = new Uri(sp.GetRequiredService<IOptions<XOptions>>().Value.Url));
                } }
                """),
            ("C.cs", """class C(System.Net.Http.HttpClient h) { Task M() => h.GetAsync("/a"); }"""));
        Assert.Equal("Real:Url", Assert.Single(hits).ConfigKey);
    }
}
