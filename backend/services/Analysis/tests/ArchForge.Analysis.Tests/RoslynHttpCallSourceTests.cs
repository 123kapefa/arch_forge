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
}
