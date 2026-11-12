using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ArchForge.Map.Core;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

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
        var (app, http) = await InternalHost("Production", "s3cret", IPAddress.Loopback);
        using var _ = app;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(WithKey("s3cret"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(WithKey(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(WithKey("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(WithKey("s3cret "))).StatusCode);

        var (remoteApp, remote) = await InternalHost("Production", "s3cret", IPAddress.Parse("198.51.100.7"));
        using var __ = remoteApp;
        Assert.Equal(HttpStatusCode.Unauthorized, (await remote.SendAsync(WithKey("s3cret"))).StatusCode);   // верный ключ не с loopback
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
        b.Services.AddCore("http://analysis", "s3cret");
        b.Services.AddHttpClient<ArchForge.Analysis.Contracts.AnalysisClient>().ConfigurePrimaryHttpMessageHandler(() => new Capture(r => seen = r.Headers.TryGetValues(AuthExtensions.InternalKeyHeader, out var v) ? v.Single() : null));
        await using var app = b.Build();

        var client = app.Services.GetRequiredService<ArchForge.Analysis.Contracts.AnalysisClient>();
        try { await client.GetRulesAsync(); } catch { /* ответ заглушки не важен */ }

        Assert.Equal("s3cret", seen);
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
