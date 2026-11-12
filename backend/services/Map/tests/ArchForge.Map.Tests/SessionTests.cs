using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using ArchForge.Map.Core;
using ArchForge.Map.Core.Auth;
using ArchForge.Map.Infrastructure;
using ArchForge.SharedKernel;
using ArchForge.SharedKernel.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArchForge.Map.Tests;

// Сессия входа (adr/0013, срез 2): код + PKCE, хранение, обновление, автономный срок 14 дней, защита от отката часов.
// Сервер входа — подделка, Keycloak не нужен; время — управляемые часы.
public class SessionTests
{
    private const string Issuer = "https://id.test/realms/archforge";
    private const string Audience = "archforge";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "k1" };

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan t) => Now += t;
    }

    private sealed class MemoryVault : ISessionVault
    {
        public StoredSession? Value { get; set; }
        public string Storage => "memory";
        public Task<StoredSession?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task SaveAsync(StoredSession s, CancellationToken ct = default) { Value = s; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Value = null; return Task.CompletedTask; }
    }

    private sealed class FakeOidc(Clock clock) : IOidcClient
    {
        public string? LastChallenge, LastVerifier, LastState;
        public OidcException? RefreshFails;
        public int Refreshes;
        public string TokenIssuer = Issuer, TokenAudience = Audience;
        public bool OmitRefresh;
        public int ExpiresIn = 300;

        public string Jwt(string sub = "user-1") => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TokenIssuer, Audience = TokenAudience, IssuedAt = clock.Now.UtcDateTime, NotBefore = clock.Now.UtcDateTime.AddSeconds(-5),
            Expires = clock.Now.UtcDateTime.AddSeconds(ExpiresIn),
            Claims = new Dictionary<string, object> { ["sub"] = sub, ["role"] = new[] { "member" }, ["permissions"] = new[] { "archforge.use" } },
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });

        public Task<Uri> BuildAuthorizeUrlAsync(string redirectUri, string state, string codeChallenge, CancellationToken ct = default)
        { LastState = state; LastChallenge = codeChallenge; return Task.FromResult(new Uri("https://id.test/auth?state=" + state)); }

        public Task<TokenSet> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
        { LastVerifier = codeVerifier; return Task.FromResult(new TokenSet(Jwt(), OmitRefresh ? null : "refresh-1", ExpiresIn)); }

        public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            Refreshes++;
            if (RefreshFails is not null) throw RefreshFails;
            return Task.FromResult(new TokenSet(Jwt(), "refresh-" + (Refreshes + 1), ExpiresIn));
        }
    }

    private static (SessionService svc, Clock clock, MemoryVault vault, FakeOidc oidc) Make(int graceDays = 14)
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var vault = new MemoryVault();
        var oidc = new FakeOidc(clock);
        var opts = Options.Create(new LoginSessionOptions { GraceDays = graceDays });
        return (new SessionService(vault, oidc, opts, new AuthOptions { Authority = Issuer, Audience = Audience }, clock, NullLogger<SessionService>.Instance), clock, vault, oidc);
    }

    private static async Task Login((SessionService svc, Clock clock, MemoryVault vault, FakeOidc oidc) t)
    {
        await t.svc.BeginLoginAsync("http://localhost:5200/api/auth/callback");
        await t.svc.CompleteLoginAsync(t.oidc.LastState!, "code");
    }

    // ── вход ──

    [Fact]
    public async Task Login_creates_active_session_with_14_day_grace()
    {
        var t = Make();
        await Login(t);

        var st = await t.svc.GetStatusAsync();

        Assert.Equal("active", st.State);
        Assert.Equal("user-1", st.Subject);
        Assert.Equal(t.clock.Now.AddDays(14), st.GraceEndsAt);
        Assert.Equal(14, st.DaysLeft);
        Assert.False(st.Warn);
        Assert.NotNull(t.vault.Value);
    }

    [Fact]
    public async Task Pkce_challenge_matches_verifier_sent_on_exchange()
    {
        var t = Make();
        await Login(t);

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(t.oidc.LastVerifier!))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, t.oidc.LastChallenge);
    }

    [Fact]
    public async Task Login_state_is_one_time_and_expires()
    {
        var t = Make();
        await t.svc.BeginLoginAsync("http://localhost:5200/api/auth/callback");
        var state = t.oidc.LastState!;
        await t.svc.CompleteLoginAsync(state, "c");

        await Assert.ThrowsAsync<OidcException>(() => t.svc.CompleteLoginAsync(state, "c"));              // повтор
        await Assert.ThrowsAsync<OidcException>(() => t.svc.CompleteLoginAsync("forged", "c"));           // чужое состояние

        await t.svc.BeginLoginAsync("http://localhost:5200/api/auth/callback");
        var late = t.oidc.LastState!;
        t.clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<OidcException>(() => t.svc.CompleteLoginAsync(late, "c"));               // устарело
    }

    [Fact]
    public async Task Pending_logins_are_capped()
    {
        var t = Make();
        for (var i = 0; i < 20; i++) await t.svc.BeginLoginAsync("http://localhost:5200/api/auth/callback");

        await Assert.ThrowsAsync<InvalidOperationException>(() => t.svc.BeginLoginAsync("http://localhost:5200/api/auth/callback"));
    }

    [Fact]
    public async Task Tokens_from_wrong_issuer_audience_or_without_refresh_are_refused()
    {
        var t = Make(); t.oidc.TokenIssuer = "https://evil.test/realms/x";
        await t.svc.BeginLoginAsync("r");
        await Assert.ThrowsAsync<OidcException>(() => t.svc.CompleteLoginAsync(t.oidc.LastState!, "c"));
        Assert.Null(t.vault.Value);

        t = Make(); t.oidc.TokenAudience = "other";
        await t.svc.BeginLoginAsync("r");
        await Assert.ThrowsAsync<OidcException>(() => t.svc.CompleteLoginAsync(t.oidc.LastState!, "c"));

        t = Make(); t.oidc.OmitRefresh = true;
        await t.svc.BeginLoginAsync("r");
        await Assert.ThrowsAsync<OidcException>(() => t.svc.CompleteLoginAsync(t.oidc.LastState!, "c"));
        Assert.Equal("none", (await t.svc.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Logout_clears_session()
    {
        var t = Make();
        await Login(t);

        await t.svc.LogoutAsync();

        Assert.Equal("none", (await t.svc.GetStatusAsync()).State);
        Assert.Null(t.vault.Value);
        Assert.Null(await t.svc.GetUsableAsync());
    }

    // ── обновление и автономный срок ──

    [Fact]
    public async Task Refresh_rotates_tokens_and_extends_grace_from_now()
    {
        var t = Make();
        await Login(t);
        t.clock.Advance(TimeSpan.FromMinutes(4));       // токен (5 мин) почти истёк

        await t.svc.TickAsync();

        Assert.Equal(1, t.oidc.Refreshes);
        Assert.Equal("refresh-2", t.vault.Value!.RefreshToken);
        Assert.Equal(t.clock.Now, t.vault.Value.LastOnlineUtc);
        Assert.Equal(t.clock.Now.AddDays(14), (await t.svc.GetStatusAsync()).GraceEndsAt);
    }

    [Fact]
    public async Task Tick_does_not_refresh_while_token_is_fresh()
    {
        var t = Make();
        await Login(t);
        t.oidc.ExpiresIn = 300;

        await t.svc.TickAsync();

        Assert.Equal(0, t.oidc.Refreshes);
    }

    [Fact]
    public async Task Network_failure_keeps_session_offline_then_warns_then_expires()
    {
        var t = Make();
        await Login(t);
        t.oidc.RefreshFails = new OidcException(OidcFailure.Network, "нет сети");
        t.clock.Advance(TimeSpan.FromMinutes(4));
        await t.svc.TickAsync();

        var st = await t.svc.GetStatusAsync();
        Assert.Equal("offline", st.State);
        Assert.NotNull(await t.svc.GetUsableAsync());               // работаем по кешу
        Assert.False(st.Warn);

        t.clock.Advance(TimeSpan.FromDays(11));                     // до конца ≈ 3 дня
        st = await t.svc.GetStatusAsync();
        Assert.True(st.Warn);
        Assert.InRange(st.DaysLeft!.Value, 2, 3);
        Assert.NotNull(await t.svc.GetUsableAsync());

        t.clock.Advance(TimeSpan.FromDays(4));                      // вышли за 14 дней
        st = await t.svc.GetStatusAsync();
        Assert.Equal("expired", st.State);
        Assert.Null(st.Subject);
        Assert.Null(await t.svc.GetUsableAsync());
    }

    [Fact]
    public async Task Reconnect_after_offline_returns_to_active_and_resets_grace()
    {
        var t = Make();
        await Login(t);
        t.oidc.RefreshFails = new OidcException(OidcFailure.Network, "нет сети");
        t.clock.Advance(TimeSpan.FromDays(5));
        await t.svc.TickAsync();
        Assert.Equal("offline", (await t.svc.GetStatusAsync()).State);

        t.oidc.RefreshFails = null;
        await t.svc.TickAsync();

        var st = await t.svc.GetStatusAsync();
        Assert.Equal("active", st.State);
        Assert.Equal(t.clock.Now.AddDays(14), st.GraceEndsAt);
    }

    [Fact]
    public async Task Server_rejection_clears_session_because_access_was_revoked()
    {
        var t = Make();
        await Login(t);
        t.oidc.RefreshFails = new OidcException(OidcFailure.Rejected, "отозван");
        t.clock.Advance(TimeSpan.FromMinutes(4));

        await t.svc.TickAsync();

        Assert.Equal("none", (await t.svc.GetStatusAsync()).State);
        Assert.Null(t.vault.Value);
    }

    // ── часы ──

    [Fact]
    public async Task Clock_rolled_back_makes_session_untrusted_and_does_not_extend_it()
    {
        var t = Make();
        await Login(t);
        t.oidc.RefreshFails = new OidcException(OidcFailure.Network, "нет сети");
        t.clock.Advance(TimeSpan.FromDays(13));
        await t.svc.GetStatusAsync();                               // приложение увидело «сегодня + 13 дней»

        t.clock.Advance(TimeSpan.FromDays(-10));                    // часы откручены, чтобы «продлить» срок
        var st = await t.svc.GetStatusAsync();

        Assert.Equal("clock", st.State);
        Assert.Null(await t.svc.GetUsableAsync());
        Assert.Equal(t.clock.Now.AddDays(10), t.vault.Value!.LastSeenUtc);   // метка времени не откатилась
    }

    [Fact]
    public async Task Small_clock_adjustments_are_tolerated()
    {
        var t = Make();
        await Login(t);
        t.clock.Advance(TimeSpan.FromHours(1));
        await t.svc.GetStatusAsync();

        t.clock.Advance(TimeSpan.FromMinutes(-3));

        Assert.Equal("active", (await t.svc.GetStatusAsync()).State);
    }

    [Fact]
    public async Task New_login_recovers_from_clock_state()
    {
        var t = Make();
        await Login(t);
        t.clock.Advance(TimeSpan.FromDays(2));
        await t.svc.GetStatusAsync();
        t.clock.Advance(TimeSpan.FromDays(-1));
        Assert.Equal("clock", (await t.svc.GetStatusAsync()).State);

        await Login(t);

        Assert.Equal("active", (await t.svc.GetStatusAsync()).State);
    }

    // ── хранилище ──

    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "af_vault_" + Guid.NewGuid().ToString("N"))).FullName;

    private static StoredSession Sample() => new(Issuer, "user-1", ["member"], ["archforge.use"], "ACCESS-SECRET-TOKEN", DateTimeOffset.UtcNow, "REFRESH-SECRET-TOKEN", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Vault_roundtrips_and_file_has_no_plaintext_tokens()
    {
        var dir = TempDir();
        var vault = new EncryptedFileSessionVault(dir, new FileKeyProtector(dir));

        await vault.SaveAsync(Sample());
        var back = await new EncryptedFileSessionVault(dir, new FileKeyProtector(dir)).LoadAsync();

        Assert.Equal("user-1", back!.Subject);
        Assert.Equal("REFRESH-SECRET-TOKEN", back.RefreshToken);
        var raw = File.ReadAllText(Path.Combine(dir, "session.bin"), Encoding.Latin1);
        Assert.DoesNotContain("SECRET-TOKEN", raw);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "session.bin")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "session.key")));
        }
    }

    [Fact]
    public async Task Vault_treats_tampered_truncated_or_foreign_files_as_no_session()
    {
        var dir = TempDir();
        var vault = new EncryptedFileSessionVault(dir, new FileKeyProtector(dir));
        await vault.SaveAsync(Sample());
        var path = Path.Combine(dir, "session.bin");
        var raw = File.ReadAllBytes(path);

        raw[^1] ^= 0xFF;                                             // подмена шифртекста
        File.WriteAllBytes(path, raw);
        Assert.Null(await new EncryptedFileSessionVault(dir, new FileKeyProtector(dir)).LoadAsync());

        File.WriteAllBytes(path, raw[..10]);                         // обрезан
        Assert.Null(await new EncryptedFileSessionVault(dir, new FileKeyProtector(dir)).LoadAsync());

        await vault.SaveAsync(Sample());                             // файл чужого ключа
        var other = TempDir();
        File.Copy(path, Path.Combine(other, "session.bin"));
        Assert.Null(await new EncryptedFileSessionVault(other, new FileKeyProtector(other)).LoadAsync());

        await vault.ClearAsync();
        Assert.False(File.Exists(path));
    }

    // ── сквозная проводка: эндпоинты, локальная сессия ──

    private static async Task<(WebApplication app, HttpClient http, FakeOidc oidc, Clock clock)> Host(bool dev = false, string? remote = "127.0.0.1")
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        b.WebHost.UseTestServer();
        b.Configuration.AddInMemoryCollection(dev
            ? new Dictionary<string, string?> { ["Authentication:DevMode"] = "true" }
            : new Dictionary<string, string?> { ["Authentication:Authority"] = Issuer, ["Authentication:Audience"] = Audience, ["Authentication:AllowInsecureMetadata"] = "true",
                                                 ["Auth:FrontendUrl"] = "http://localhost:5173/" });
        b.Services.AddCore("http://analysis");
        b.Services.AddInfrastructure(TempDir());
        b.Services.AddArchForgeAuth(b.Configuration, b.Environment);
        b.Services.AddLocalSession(b.Configuration);
        var clock = new Clock(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var oidc = new FakeOidc(clock);
        b.Services.RemoveAll<IOidcClient>(); b.Services.AddSingleton<IOidcClient>(oidc);
        b.Services.RemoveAll<ISessionVault>(); b.Services.AddSingleton<ISessionVault>(new MemoryVault());
        b.Services.RemoveAll<TimeProvider>(); b.Services.AddSingleton<TimeProvider>(clock);
        b.Services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();   // фоновое обновление в тесте не нужно
        var app = b.Build();
        app.Use((ctx, next) => { ctx.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote); return next(); });
        app.UseArchForgeAuth();
        app.MapEndpoints();
        await app.StartAsync();
        return (app, app.GetTestClient(), oidc, clock);
    }

    private static HttpClient NoRedirect(HttpClient c) => c;

    [Fact]
    public async Task Endpoints_full_flow_login_then_local_requests_are_signed_in_then_logout()
    {
        var (app, http, oidc, _) = await Host();
        await using var _ = app;

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);          // не входили
        Assert.Equal("none", (await http.GetFromJsonAsync<SessionStatusDto>("/api/auth/session"))!.State);

        var login = await http.PostAsync("/api/auth/login", null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("state=", await login.Content.ReadAsStringAsync());

        var cb = await http.GetAsync($"/api/auth/callback?code=abc&state={oidc.LastState}");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        Assert.Equal("http://localhost:5173/?login=ok", cb.Headers.Location!.ToString());

        var st = (await http.GetFromJsonAsync<SessionStatusDto>("/api/auth/session"))!;
        Assert.Equal("active", st.State);
        Assert.Equal("user-1", st.Subject);
        var me = await http.GetFromJsonAsync<ArchForge.Map.Core.Features.Auth.MeDto>("/api/me");   // без токена: по локальной сессии
        Assert.Equal("user-1", me!.Subject);
        Assert.Equal(["archforge.use"], me.Permissions);

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Callback_with_forged_state_or_error_goes_back_as_failed_and_sets_no_session()
    {
        var (app, http, _, _) = await Host();
        await using var _ = app;

        var bad = await http.GetAsync("/api/auth/callback?code=abc&state=forged");
        Assert.Equal("http://localhost:5173/?login=failed", bad.Headers.Location!.ToString());
        var err = await http.GetAsync("/api/auth/callback?error=access_denied");
        Assert.Equal("http://localhost:5173/?login=failed", err.Headers.Location!.ToString());
        Assert.Equal("none", (await http.GetFromJsonAsync<SessionStatusDto>("/api/auth/session"))!.State);
    }

    [Fact]
    public async Task Auth_endpoints_and_local_session_are_for_this_machine_only()
    {
        var (app, http, oidc, _) = await Host(remote: "203.0.113.9");
        await using var _ = app;

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/api/auth/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/auth/callback?code=a&state=b")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);
        Assert.Null(oidc.LastState);
    }

    [Fact]
    public async Task Expired_local_session_stops_authenticating_requests()
    {
        var (app, http, oidc, clock) = await Host();
        await using var _ = app;
        await http.PostAsync("/api/auth/login", null);
        await http.GetAsync($"/api/auth/callback?code=abc&state={oidc.LastState}");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/me")).StatusCode);

        clock.Advance(TimeSpan.FromDays(15));

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);
        Assert.Equal("expired", (await http.GetFromJsonAsync<SessionStatusDto>("/api/auth/session"))!.State);
    }

    [Fact]
    public async Task Dev_mode_reports_dev_state_and_refuses_login()
    {
        var (app, http, _, _) = await Host(dev: true);
        await using var _ = app;

        Assert.Equal("dev", (await http.GetFromJsonAsync<SessionStatusDto>("/api/auth/session"))!.State);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/auth/login", null)).StatusCode);
    }

    [Fact]
    public void Session_options_are_validated_at_startup()
    {
        foreach (var bad in new[] { ("Auth:GraceDays", "0"), ("Auth:GraceDays", "999"), ("Auth:WarnDays", "20"),
                                    ("Auth:RedirectBaseUrl", "https://localhost:5200"), ("Auth:RedirectBaseUrl", "http://evil.example"), ("Auth:FrontendUrl", "nope") })
        {
            var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Authority"] = Issuer, ["Authentication:Audience"] = Audience, [bad.Item1] = bad.Item2 });
            Assert.Throws<InvalidOperationException>(() => b.Services.AddLocalSession(b.Configuration));
        }
    }
}
