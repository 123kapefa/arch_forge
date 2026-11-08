using System.Net;
using System.Net.Http.Json;
using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;
using ArchForge.Map.Core;
using ArchForge.Map.Core.Profiles;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace ArchForge.Map.Tests;

// Сквозная проводка Map: DI, маршруты, привязка JSON, коды ответов (adr/0012). Analysis — стаб.
public class ProfileEndpointsTests : IAsyncLifetime
{
    private sealed class AnalysisStub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Calls { get; } = [];
        public bool Down { get; set; }                 // Analysis недоступен (отказ соединения)
        public bool RejectProfile { get; set; }        // Analysis отвечает 400 на POST /api/system (профиль не принят)
        public bool RejectPreview { get; set; }        // Analysis отвечает 400 на POST /api/system/preview
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            if (Down) throw new HttpRequestException("connection refused");
            if (request.RequestUri!.AbsolutePath == "/api/system/preview")
            {
                if (RejectPreview) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                var none = new Dictionary<string, RuleImpactDto>();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PreviewDto(
                    new ImpactSummaryDto(1, 2, 3, none), new ImpactSummaryDto(4, 5, 6, none),
                    new LevelHintDto("Standard", ["сервисов в compose: 2"]), new Dictionary<string, int> { ["ICommand"] = 7 }, new Dictionary<string, int>())) };
            }
            if (request.RequestUri!.AbsolutePath == "/api/vocabulary")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new VocabularyDefaultsDto(
                    new Dictionary<string, RoleVocabularyDto> { ["Command"] = new(["ICommand"], [], ["Command"]) },
                    new ServiceNamingDto(["-api"], ["-migrate"], ["seed"]), new ProfileParamsDto(0.8, 3, 3))) };
            if (request.RequestUri!.AbsolutePath == "/api/rules") return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Catalog) };
            if (RejectProfile && request.Method == HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GraphDto("sys", [], [], new CoverageDto(0, 0, 0, 0), [], Health: "ok")),
            };
        }
    }

    private static readonly RuleCatalogDto Catalog = new("2026.11",
        [new RuleDto("drift", "convention", "Дрейф", "n", new Dictionary<string, string?> { ["Simple"] = "Off", ["Standard"] = null, ["Platform"] = null })]);

    private readonly AnalysisStub _analysis = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private MemoryStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new MemoryStore();
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddCore("http://analysis");
        b.Services.RemoveAll<IProfileStore>();
        b.Services.AddSingleton<IProfileStore>(_store);
        b.Services.AddHttpClient<AnalysisClient>().ConfigurePrimaryHttpMessageHandler(() => _analysis);
        _app = b.Build();
        _app.UseCrossSiteGuard();
        _app.MapEndpoints();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync() { _http.Dispose(); await _app.DisposeAsync(); }

    private static string Proj => Path.Combine(Path.GetTempPath(), "ep_proj");
    private string ProfileUrl => "/api/profile?path=" + Uri.EscapeDataString(Proj);

    [Fact]
    public async Task Full_flow_create_bind_and_system_uses_profile()
    {
        // 1) создать конфигурацию
        var create = await _http.PostAsJsonAsync("/api/profile/configs",
            new ConfigManifestDto(null, "Строгая", "@kapefa", 1, new ProfileDto("Platform", new Dictionary<string, string> { ["drift"] = "Error" })));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var cfg = (await create.Content.ReadFromJsonAsync<ConfigManifestDto>())!;
        Assert.Equal("own", cfg.Kind);

        // 2) до привязки /api/system идёт прежним GET (профиль по умолчанию)
        await _http.GetAsync("/api/system?path=" + Uri.EscapeDataString(Proj));
        Assert.Equal(HttpMethod.Get, _analysis.Calls[^1].Method);

        // 3) привязать проект
        var bind = await _http.PutAsJsonAsync(ProfileUrl, new ProfileBindingDto(ConfigId: cfg.Id));
        Assert.Equal(HttpStatusCode.OK, bind.StatusCode);
        var got = (await _http.GetFromJsonAsync<ProfileForPathDto>(ProfileUrl))!;
        Assert.Equal(cfg.Id, got.Binding.ConfigId);
        Assert.Equal("Platform", got.Effective.Level);

        // 4) теперь /api/system уходит в Analysis POST-ом с профилем
        var sys = await _http.GetAsync("/api/system?path=" + Uri.EscapeDataString(Proj) + "&drift=true");
        Assert.Equal(HttpStatusCode.OK, sys.StatusCode);
        var call = _analysis.Calls[^1];
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Contains("drift=true", call.Url);
        Assert.Contains("Platform", call.Body);
        Assert.Contains("Error", call.Body);
    }

    [Fact]
    public async Task Invalid_config_returns_400_with_error_list()
    {
        var r = await _http.PostAsJsonAsync("/api/profile/configs",
            new ConfigManifestDto(null, "", "a", 1, new ProfileDto("Huge", new Dictionary<string, string> { ["ghost"] = "Off" })));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("errors", body);
        Assert.Contains("ghost", body);
    }

    [Fact]
    public async Task Missing_body_is_400_not_500()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync<ConfigManifestDto?>("/api/profile/configs", null)).StatusCode);
    }

    [Fact]
    public async Task Update_conflict_is_409_and_builtin_is_403_and_unknown_is_404()
    {
        var cfg = (await (await _http.PostAsJsonAsync("/api/profile/configs",
            new ConfigManifestDto(null, "A", "a", 1, new ProfileDto("Standard")))).Content.ReadFromJsonAsync<ConfigManifestDto>())!;

        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync($"/api/profile/configs/{cfg.Id}", cfg)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PutAsJsonAsync($"/api/profile/configs/{cfg.Id}", cfg)).StatusCode);   // версия 1 уже устарела
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PutAsJsonAsync("/api/profile/configs/lvl-standard", cfg)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/profile/configs/ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.DeleteAsync("/api/profile/configs/lvl-simple")).StatusCode);
    }

    [Fact]
    public async Task Delete_and_duplicate_and_list()
    {
        var cfg = (await (await _http.PostAsJsonAsync("/api/profile/configs",
            new ConfigManifestDto(null, "A", "a", 1, new ProfileDto("Standard")))).Content.ReadFromJsonAsync<ConfigManifestDto>())!;

        var dup = await _http.PostAsync($"/api/profile/configs/{cfg.Id}/duplicate?author=%40marta", null);
        Assert.Equal(HttpStatusCode.Created, dup.StatusCode);
        var list = (await _http.GetFromJsonAsync<List<ConfigManifestDto>>("/api/profile/configs"))!;
        Assert.Equal(5, list.Count);                                      // 3 уровня + оригинал + копия

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/profile/configs/{cfg.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync($"/api/profile/configs/{cfg.Id}")).StatusCode);
    }

    [Fact]
    public async Task Bind_validation_errors_are_4xx()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PutAsJsonAsync(ProfileUrl, new ProfileBindingDto("x", "Simple"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PutAsJsonAsync(ProfileUrl, new ProfileBindingDto(ConfigId: "ghost"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/profile?path=")).StatusCode);
    }

    [Fact]
    public async Task Rules_endpoint_returns_catalog_and_503_without_analysis()
    {
        var rules = (await _http.GetFromJsonAsync<RuleCatalogDto>("/api/rules"))!;
        Assert.Equal("drift", rules.Rules.Single().Id);
    }

    [Fact]
    public async Task Body_larger_than_limit_is_not_a_server_error()
    {
        // сам лимит тела задаётся в Program.cs (Kestrel); здесь — что громоздкий словарь отклоняется валидатором, а не роняет сервис
        var voc = Enumerable.Range(0, 500).ToDictionary(i => "R" + i, _ => new RoleVocabularyDto(["M"]));

        var r = await _http.PostAsJsonAsync("/api/profile/configs",
            new ConfigManifestDto(null, "Big", "a", 1, new ProfileDto("Standard", Vocabulary: voc)));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task System_without_binding_keeps_old_behaviour_and_survives_store_failure()
    {
        _store.Throw = true;

        var r = await _http.GetAsync("/api/system?path=" + Uri.EscapeDataString(Proj));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);                    // профиль не прочитан → разбор по умолчанию
        Assert.Equal(HttpMethod.Get, _analysis.Calls[^1].Method);
    }

    // ── ревью среза 2 ──

    [Fact]
    public async Task Created_response_points_to_the_new_resource()
    {
        var r = await _http.PostAsJsonAsync("/api/profile/configs", new ConfigManifestDto(null, "A", "a", 1, new ProfileDto("Standard")));
        var cfg = (await r.Content.ReadFromJsonAsync<ConfigManifestDto>())!;

        Assert.Equal($"/api/profile/configs/{cfg.Id}", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Analysis_down_gives_503_not_500()
    {
        _analysis.Down = true;

        var create = await _http.PostAsJsonAsync("/api/profile/configs", new ConfigManifestDto(null, "A", "a", 1, new ProfileDto("Standard")));
        var rules = await _http.GetAsync("/api/rules");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rules.StatusCode);
    }

    [Fact]
    public async Task Corrupt_store_gives_503_with_message_and_no_path()
    {
        _store.Throw = true;

        var r = await _http.GetAsync("/api/profile/configs");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("error", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Profile_rejected_by_analysis_does_not_blank_the_map()
    {
        var cfg = (await (await _http.PostAsJsonAsync("/api/profile/configs",
            new ConfigManifestDto(null, "A", "a", 1, new ProfileDto("Platform")))).Content.ReadFromJsonAsync<ConfigManifestDto>())!;
        await _http.PutAsJsonAsync(ProfileUrl, new ProfileBindingDto(ConfigId: cfg.Id));
        _analysis.RejectProfile = true;                                    // например, правило убрали из каталога после сохранения

        var r = await _http.GetAsync("/api/system?path=" + Uri.EscapeDataString(Proj));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);                     // карта есть
        var g = (await r.Content.ReadFromJsonAsync<GraphDto>())!;
        Assert.Contains(g.Degradations!, d => d.Contains("профиль"));      // и честно сказано, что профиль не применён
        Assert.Equal(HttpMethod.Get, _analysis.Calls[^1].Method);
    }

    [Fact]
    public async Task Put_without_body_is_400_and_empty_object_resets()
    {
        await _http.PutAsJsonAsync(ProfileUrl, new ProfileBindingDto(Level: "Simple"));

        var none = await _http.PutAsync(ProfileUrl, new StringContent("null", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("Simple", (await _http.GetFromJsonAsync<ProfileForPathDto>(ProfileUrl))!.Effective.Level);   // не сброшено

        var reset = await _http.PutAsJsonAsync(ProfileUrl, new ProfileBindingDto());
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal("Standard", (await _http.GetFromJsonAsync<ProfileForPathDto>(ProfileUrl))!.Effective.Level);
    }

    [Theory]
    [InlineData("POST", "/api/profile/configs/lvl-standard/duplicate")]
    [InlineData("PUT", "/api/profile?path=%2Ftmp%2Fx")]
    [InlineData("DELETE", "/api/profile/configs/ghost")]
    public async Task Cross_site_mutations_are_forbidden_but_same_site_and_plain_clients_pass(string method, string url)
    {
        using var cross = new HttpRequestMessage(new HttpMethod(method), url);
        cross.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var same = new HttpRequestMessage(new HttpMethod(method), url);
        same.Headers.Add("Sec-Fetch-Site", "same-site");                    // фронт на другом порту той же машины
        using var plain = new HttpRequestMessage(new HttpMethod(method), url);

        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(cross)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await _http.SendAsync(same)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await _http.SendAsync(plain)).StatusCode);
    }

    [Fact]
    public async Task Cross_site_reads_are_not_blocked_by_the_guard()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/profile/configs");
        req.Headers.Add("Sec-Fetch-Site", "cross-site");

        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(req)).StatusCode);   // чтение защищено same-origin политикой браузера
    }

    [Fact]
    public async Task Vocabulary_is_proxied_and_503_when_analysis_down()
    {
        var v = (await _http.GetFromJsonAsync<VocabularyDefaultsDto>("/api/vocabulary"))!;
        Assert.Equal(["ICommand"], v.Roles["Command"].Markers!);

        _analysis.Down = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.GetAsync("/api/vocabulary")).StatusCode);
    }

    // ── предпросмотр (#127) ──

    [Fact]
    public async Task Preview_is_proxied_with_the_profile_in_the_body()
    {
        var r = await _http.PostAsJsonAsync("/api/profile/preview?path=" + Uri.EscapeDataString(Proj),
            new ProfileDto("Simple", new Dictionary<string, string> { ["drift"] = "Warning" }));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var pv = (await r.Content.ReadFromJsonAsync<PreviewDto>())!;
        Assert.Equal(1, pv.Current.Errors);
        Assert.Equal(7, pv.MarkerCounts["ICommand"]);
        var call = _analysis.Calls.Last(c => c.Url.StartsWith("/api/system/preview"));
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Contains("drift", call.Body);
    }

    [Fact]
    public async Task Preview_validates_the_profile_before_calling_analysis()
    {
        var before = _analysis.Calls.Count(c => c.Url.StartsWith("/api/system/preview"));

        var r = await _http.PostAsJsonAsync("/api/profile/preview?path=" + Uri.EscapeDataString(Proj),
            new ProfileDto("Huge", new Dictionary<string, string> { ["ghost"] = "Off" }));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(before, _analysis.Calls.Count(c => c.Url.StartsWith("/api/system/preview")));   // до Analysis не дошло
        Assert.Contains("ghost", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preview_rejects_missing_body_and_bad_path()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync<ProfileDto?>("/api/profile/preview?path=" + Uri.EscapeDataString(Proj), null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/profile/preview?path=", new ProfileDto("Standard"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/profile/preview?path=" + new string('p', 2000), new ProfileDto("Standard"))).StatusCode);
    }

    [Fact]
    public async Task Preview_is_502_when_analysis_refuses_or_drops_after_catalog_was_cached()
    {
        _analysis.RejectPreview = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await _http.PostAsJsonAsync("/api/profile/preview?path=" + Uri.EscapeDataString(Proj), new ProfileDto("Standard"))).StatusCode);

        _analysis.RejectPreview = false; _analysis.Down = true;   // каталог уже в кеше → проверка профиля проходит, падает сам расчёт
        Assert.Equal(HttpStatusCode.BadGateway, (await _http.PostAsJsonAsync("/api/profile/preview?path=" + Uri.EscapeDataString(Proj), new ProfileDto("Standard"))).StatusCode);
    }

    [Fact]
    public async Task Preview_is_503_when_catalog_is_unavailable_from_the_start()
    {
        _analysis.Down = true;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.PostAsJsonAsync("/api/profile/preview?path=" + Uri.EscapeDataString(Proj), new ProfileDto("Standard"))).StatusCode);
    }

    [Fact]
    public async Task Preview_post_from_another_site_is_forbidden()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/profile/preview?path=%2Ftmp%2Fx") { Content = JsonContent.Create(new ProfileDto("Standard")) };
        req.Headers.Add("Sec-Fetch-Site", "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Map_passes_refresh_to_analysis_for_system()
    {
        await _http.GetAsync("/api/system?path=" + Uri.EscapeDataString(Proj) + "&refresh=true");
        Assert.Contains("refresh=true", _analysis.Calls[^1].Url);

        await _http.GetAsync("/api/system?path=" + Uri.EscapeDataString(Proj));
        Assert.DoesNotContain("refresh", _analysis.Calls[^1].Url);
    }
}

