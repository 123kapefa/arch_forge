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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
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
        b.Services.RemoveAll<IRuleCatalogProvider>();
        b.Services.AddSingleton<IRuleCatalogProvider>(new FakeCatalog(Catalog));
        b.Services.AddHttpClient<AnalysisClient>().ConfigurePrimaryHttpMessageHandler(() => _analysis);
        _app = b.Build();
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
}
