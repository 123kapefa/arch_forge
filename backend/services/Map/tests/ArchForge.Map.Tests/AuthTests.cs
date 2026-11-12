using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ArchForge.Map.Core;
using ArchForge.SharedKernel;
using ArchForge.SharedKernel.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ArchForge.Map.Tests;

// Вход (adr/0013, срез 1): проверка токена, режим разработки, закрытие внутреннего канала. Токены — фиктивный издатель
// (свой RSA-ключ), Keycloak не нужен.
public class AuthTests
{
    private const string Issuer = "https://id.test/realms/archforge";
    private const string Audience = "archforge";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "k1" };
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "k1" };   // тот же kid, чужая подпись

    private static string Token(Action<SecurityTokenDescriptor>? tweak = null, SecurityKey? key = null, string alg = SecurityAlgorithms.RsaSha256, string[]? perms = null)
    {
        var d = new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = "user-1", ["role"] = new[] { "member" }, ["permissions"] = perms ?? ["archforge.use"] },
            SigningCredentials = new SigningCredentials(key ?? SigningKey, alg),
        };
        tweak?.Invoke(d);
        return new JsonWebTokenHandler().CreateToken(d);
    }

    private static async Task<(WebApplication app, HttpClient http)> Host(string env, Dictionary<string, string?> cfg, IPAddress? remote = null, bool jwtOffline = true)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = env });
        b.WebHost.UseTestServer();
        b.Configuration.AddInMemoryCollection(cfg);
        b.Services.AddArchForgeAuth(b.Configuration, b.Environment);
        if (jwtOffline && cfg.ContainsKey("Authentication:Authority"))
            b.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var c = new OpenIdConnectConfiguration { Issuer = Issuer };
                c.SigningKeys.Add(SigningKey);
                o.Configuration = c;
                o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(c);   // ключи из «discovery» без сети
            });
        var app = b.Build();
        app.Use((ctx, next) => { ctx.Connection.RemoteIpAddress = remote; return next(); });
        app.UseArchForgeAuth();
        app.MapGet("/api/me", (HttpContext c, [Microsoft.AspNetCore.Mvc.FromServices] AuthMode m) => new { sub = c.User.FindFirst("sub")?.Value, dev = m.DevMode,
            perms = c.User.FindAll("permissions").Select(x => x.Value).ToArray() }).RequireAuthorization(AuthExtensions.UserPolicy);
        app.MapGet("/admin", () => "ok").RequirePermissions("archforge.admin");
        app.MapGet("/use", () => "ok").RequirePermissions("archforge.use");
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static Dictionary<string, string?> Jwt() => new() { ["Authentication:Authority"] = Issuer, ["Authentication:Audience"] = Audience };

    private static HttpRequestMessage Get(string url, string? token = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    // ── настройка: небезопасные сочетания не стартуют ──

    [Fact]
    public async Task Startup_fails_without_authority_and_without_dev_mode()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Development", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Production", []));
    }

    [Fact]
    public async Task Dev_mode_is_refused_outside_development_and_with_authority()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Production", new() { ["Authentication:DevMode"] = "true" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Staging", new() { ["Authentication:DevMode"] = "true" }));
        var both = Jwt(); both["Authentication:DevMode"] = "true";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Development", both));
    }

    [Fact]
    public async Task Authority_must_be_https_and_audience_required()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Production", new() { ["Authentication:Authority"] = "http://id.test/realms/x", ["Authentication:Audience"] = "a" }));
        // http допустим только с явным флагом и только в Development
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Production", new() { ["Authentication:Authority"] = "http://id.test/realms/x", ["Authentication:Audience"] = "a", ["Authentication:AllowInsecureMetadata"] = "true" }));
        var (app, _) = await Host("Development", new() { ["Authentication:Authority"] = "http://id.test/realms/x", ["Authentication:Audience"] = "a", ["Authentication:AllowInsecureMetadata"] = "true" }, jwtOffline: false);
        await app.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Production", new() { ["Authentication:Authority"] = Issuer }));
    }

    // ── режим разработки ──

    [Fact]
    public async Task Dev_mode_gives_limited_dev_identity_to_loopback_only()
    {
        var cfg = new Dictionary<string, string?> { ["Authentication:DevMode"] = "true" };
        var (app, http) = await Host("Development", cfg, IPAddress.Loopback);
        var me = await http.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>("/api/me");
        Assert.Equal("dev", me!["sub"].GetString());
        Assert.True(me["dev"].GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/use")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/admin")).StatusCode);   // не администратор
        await app.DisposeAsync();

        var (remoteApp, remote) = await Host("Development", cfg, IPAddress.Parse("203.0.113.5"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await remote.GetAsync("/api/me")).StatusCode);
        await remoteApp.DisposeAsync();
        var (noIpApp, noIp) = await Host("Development", cfg, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await noIp.GetAsync("/api/me")).StatusCode);
        await noIpApp.DisposeAsync();
    }

    // ── проверка токена ──

    [Fact]
    public async Task Valid_token_passes_and_exposes_claims()
    {
        var (app, http) = await Host("Production", Jwt(), IPAddress.Loopback);
        using var _ = app;

        var res = await http.SendAsync(Get("/api/me", Token()));

        Assert.True(res.StatusCode == HttpStatusCode.OK, string.Join(" | ", res.Headers.WwwAuthenticate.Select(h => h.ToString())));
        var me = await res.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.Equal("user-1", me!["sub"].GetString());
        Assert.False(me["dev"].GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Get("/use", Token()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Get("/admin", Token()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Get("/admin", Token(perms: ["archforge.admin"])))).StatusCode);
    }

    [Fact]
    public async Task Missing_or_broken_tokens_get_401()
    {
        var (app, http) = await Host("Production", Jwt(), IPAddress.Loopback);
        using var _ = app;

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);                       // без токена
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", "garbage"))).StatusCode);       // мусор
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", Token(d => d.Audience = "other")))).StatusCode);       // чужой адресат
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", Token(d => d.Issuer = "https://evil.test/realms/x")))).StatusCode);   // чужой издатель
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", Token(d => { d.NotBefore = DateTime.UtcNow.AddMinutes(-9); d.Expires = DateTime.UtcNow.AddMinutes(-1); })))).StatusCode);   // истёк
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", Token(key: OtherKey)))).StatusCode);   // чужая подпись
        var hmac = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", Token(key: hmac, alg: SecurityAlgorithms.HmacSha256)))).StatusCode);   // другой алгоритм
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/admin", Token(d => d.Audience = "other", perms: ["archforge.admin"])))).StatusCode);
    }

    [Fact]
    public async Task Token_from_query_string_or_cookie_is_ignored()
    {
        var (app, http) = await Host("Production", Jwt(), IPAddress.Loopback);
        using var _ = app;
        var t = Token();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me?access_token=" + t)).StatusCode);
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/me"); r.Headers.Add("Cookie", "access_token=" + t);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(r)).StatusCode);
    }

    [Fact]
    public async Task Real_me_endpoint_returns_roles_and_permissions_from_token()
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        b.WebHost.UseTestServer();
        b.Configuration.AddInMemoryCollection(Jwt());
        b.Services.AddCore("http://analysis");
        b.Services.AddArchForgeAuth(b.Configuration, b.Environment);
        b.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            var c = new OpenIdConnectConfiguration { Issuer = Issuer };
            c.SigningKeys.Add(SigningKey);
            o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(c);
        });
        await using var app = b.Build();
        app.UseArchForgeAuth();
        app.MapEndpoints();   // настоящие эндпоинты Map, включая /api/me
        await app.StartAsync();
        var http = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);
        var me = await (await http.SendAsync(Get("/api/me", Token(perms: ["archforge.use", "x.y"])))).Content.ReadFromJsonAsync<ArchForge.Map.Core.Features.Auth.MeDto>();
        Assert.Equal("user-1", me!.Subject);
        Assert.Equal(["member"], me.Roles);
        Assert.Equal(["archforge.use", "x.y"], me.Permissions);
        Assert.False(me.DevMode);
    }

    [Fact]
    public async Task Require_permissions_needs_all_listed_permissions_and_future_or_unsigned_tokens_fail()
    {
        var (app, http) = await Host("Production", Jwt(), IPAddress.Loopback);
        using var _ = app;
        app.MapGet("/both", () => "ok").RequirePermissions("archforge.use", "archforge.admin");

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Get("/both", Token()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Get("/both", Token(perms: ["archforge.use", "archforge.admin"])))).StatusCode);
        // nbf в будущем (сверх допуска часов)
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", Token(d => { d.NotBefore = DateTime.UtcNow.AddMinutes(5); d.Expires = DateTime.UtcNow.AddMinutes(15); })))).StatusCode);
        // alg=none
        static string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=');
        var none = B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + B64("{\"sub\":\"x\",\"iss\":\"" + Issuer + "\",\"aud\":\"" + Audience + "\",\"exp\":9999999999}") + ".";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Get("/api/me", none))).StatusCode);
    }

    [Fact]
    public async Task Require_login_closes_existing_endpoints_without_token()
    {
        var cfg = Jwt(); cfg["Authentication:RequireLogin"] = "true";
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        b.WebHost.UseTestServer();
        b.Configuration.AddInMemoryCollection(cfg);
        b.Services.AddCore("http://analysis");
        b.Services.AddArchForgeAuth(b.Configuration, b.Environment);
        b.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            var c = new OpenIdConnectConfiguration { Issuer = Issuer };
            c.SigningKeys.Add(SigningKey);
            o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(c);
        });
        await using var app = b.Build();
        app.UseArchForgeAuth();
        app.MapEndpoints();
        await app.StartAsync();
        var http = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/profile/configs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Get("/api/me", Token()))).StatusCode);   // с токеном — пропускает
        // вошёл, но права пользоваться нет — закрыто (403), с правом — открыто
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Get("/api/rules", Token(perms: [])))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Get("/api/rules", Token(perms: ["other.right"])))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await http.SendAsync(Get("/api/rules", Token()))).StatusCode);
    }

    [Fact]
    public async Task Without_require_login_existing_endpoints_stay_open()
    {
        var (app, http) = await Host("Production", Jwt(), IPAddress.Loopback);
        using var _ = app;
        app.MapGet("/open", () => "ok");

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/open")).StatusCode);
    }

    [Fact]
    public async Task Error_details_are_hidden_outside_development()
    {
        var (app, http) = await Host("Production", Jwt(), IPAddress.Loopback);
        using var _ = app;

        var res = await http.SendAsync(Get("/api/me", Token(key: OtherKey)));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain(res.Headers.WwwAuthenticate, h => (h.Parameter ?? "").Contains("signature"));
    }

    [Fact]
    public async Task Internal_authority_must_be_https_too()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host("Production", new() { ["Authentication:Authority"] = Issuer, ["Authentication:Audience"] = Audience, ["Authentication:InternalAuthority"] = "http://keycloak:8080/realms/x" }));
    }

    [Fact]
    public async Task Authority_with_trailing_slash_still_matches_issuer()
    {
        var cfg = new Dictionary<string, string?> { ["Authentication:Authority"] = Issuer + "/", ["Authentication:Audience"] = Audience };
        var (app, http) = await Host("Production", cfg, IPAddress.Loopback);
        using var _ = app;

        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Get("/api/me", Token()))).StatusCode);
    }

    // ── закрытый канал Map → Analysis ──

    private static async Task<(WebApplication, HttpClient)> InternalHost(string env, string? key, IPAddress? remote)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = env });
        b.WebHost.UseTestServer();
        if (key is not null) b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Key"] = key });
        var app = b.Build();
        app.Use((ctx, next) => { ctx.Connection.RemoteIpAddress = remote; return next(); });
        app.UseInternalGuard();
        app.MapGet("/x", () => "ok");
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage WithKey(string? key) { var r = new HttpRequestMessage(HttpMethod.Get, "/x"); if (key is not null) r.Headers.Add(AuthExtensions.InternalKeyHeader, key); return r; }

    [Fact]
    public async Task Internal_guard_requires_key_and_loopback()
    {
        var (app, http) = await InternalHost("Production", "s3cret-key-0123456789", IPAddress.Loopback);
        using var _ = app;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(WithKey("s3cret-key-0123456789"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(WithKey(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(WithKey("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(WithKey("s3cret-key-0123456789 "))).StatusCode);

        var (remoteApp, remote) = await InternalHost("Production", "s3cret-key-0123456789", IPAddress.Parse("198.51.100.7"));
        using var __ = remoteApp;
        Assert.Equal(HttpStatusCode.Unauthorized, (await remote.SendAsync(WithKey("s3cret-key-0123456789"))).StatusCode);   // верный ключ не с loopback
    }

    [Fact]
    public async Task Internal_guard_without_key_is_development_only()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => InternalHost("Production", null, IPAddress.Loopback));
        var (app, http) = await InternalHost("Development", null, IPAddress.Loopback);
        using var _ = app;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(WithKey(null))).StatusCode);
        var (remoteApp, remote) = await InternalHost("Development", null, IPAddress.Parse("198.51.100.7"));
        using var __ = remoteApp;
        Assert.Equal(HttpStatusCode.Unauthorized, (await remote.SendAsync(WithKey(null))).StatusCode);
    }

    [Fact]
    public async Task Map_sends_internal_key_to_analysis()
    {
        string? seen = null;
        var b = WebApplication.CreateBuilder();
        b.Services.AddCore("http://analysis", "s3cret-key-0123456789");
        b.Services.AddHttpClient<ArchForge.Analysis.Contracts.AnalysisClient>().ConfigurePrimaryHttpMessageHandler(() => new Capture(r => seen = r.Headers.TryGetValues(AuthExtensions.InternalKeyHeader, out var v) ? v.Single() : null));
        await using var app = b.Build();

        var client = app.Services.GetRequiredService<ArchForge.Analysis.Contracts.AnalysisClient>();
        try { await client.GetRulesAsync(); } catch { /* ответ заглушки не важен */ }

        Assert.Equal("s3cret-key-0123456789", seen);
    }

    [Fact]
    public async Task Map_sends_no_key_header_when_key_is_not_configured()
    {
        string? seen = "unset";
        var b = WebApplication.CreateBuilder();
        b.Services.AddCore("http://analysis", null);
        b.Services.AddHttpClient<ArchForge.Analysis.Contracts.AnalysisClient>().ConfigurePrimaryHttpMessageHandler(() => new Capture(r => seen = r.Headers.TryGetValues(AuthExtensions.InternalKeyHeader, out var v) ? v.Single() : null));
        await using var app = b.Build();

        try { await app.Services.GetRequiredService<ArchForge.Analysis.Contracts.AnalysisClient>().GetRulesAsync(); } catch { }

        Assert.Null(seen);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("short", false)]
    [InlineData("has space in the key 12345", false)]
    [InlineData("line\nbreak-in-the-key-123456", false)]
    [InlineData("ключ-не-ascii-1234567890", false)]
    [InlineData("0123456789abcdef", true)]
    public void Internal_key_format(string? key, bool ok) => Assert.Equal(ok, AuthExtensions.IsValidInternalKey(key));

    [Fact]
    public void Map_outside_development_requires_internal_key()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prod = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };
        var dev = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" };

        Assert.Throws<InvalidOperationException>(() => AuthExtensions.ValidateInternalKey(cfg, prod));
        Assert.Null(AuthExtensions.ValidateInternalKey(cfg, dev));
    }

    private sealed class Capture(Action<HttpRequestMessage> on) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            on(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
        }
    }
}
