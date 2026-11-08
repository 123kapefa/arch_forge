using System.Net;
using System.Net.Http.Json;
using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Manifests;
using ArchForge.Analysis.Infrastructure.Roslyn;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Tests.Profiles;

// Проводка новых эндпоинтов Analysis (adr/0012): DI, маршруты, привязка тела, коды ответов.
public class ProfileEndpointsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddCore();
        b.Services.AddInfrastructure();
        b.Services.AddManifests();
        _app = b.Build();
        _app.MapEndpoints();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync() { _http.Dispose(); await _app.DisposeAsync(); }

    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_ep_" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Rules_endpoint_returns_catalog()
    {
        var dto = (await _http.GetFromJsonAsync<RuleCatalogDto>("/api/rules"))!;

        Assert.NotEmpty(dto.Rules);
        Assert.Contains(dto.Rules, r => r.Id == "service-cycle");
    }

    [Fact]
    public async Task Post_system_with_valid_profile_is_200()
    {
        var dir = TempDir();

        var r = await _http.PostAsJsonAsync($"/api/system?path={Uri.EscapeDataString(dir)}",
            new ProfileDto("Simple", new Dictionary<string, string> { ["drift"] = "Warning" }));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Post_system_with_invalid_profile_is_400_and_lists_errors()
    {
        var dir = TempDir();

        var r = await _http.PostAsJsonAsync($"/api/system?path={Uri.EscapeDataString(dir)}",
            new ProfileDto("Huge", new Dictionary<string, string> { ["ghost"] = "Loud" }));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("errors", body);
        Assert.Contains("ghost", body);
    }

    [Fact]
    public async Task Post_system_without_body_is_400_not_500()
    {
        var dir = TempDir();

        var r = await _http.PostAsJsonAsync<ProfileDto?>($"/api/system?path={Uri.EscapeDataString(dir)}", null);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Post_system_on_missing_path_is_400()
    {
        var r = await _http.PostAsJsonAsync("/api/system?path=%2Fno%2Fsuch%2Fdir_archforge", new ProfileDto("Standard"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Get_system_still_works_without_profile()
    {
        var dir = TempDir();

        var r = await _http.GetAsync($"/api/system?path={Uri.EscapeDataString(dir)}");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
