using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ArchForge.SharedKernel.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchForge.Map.Core.Auth;

public static class SessionRegistration
{
    public const string LocalScheme = "ArchForgeLocalSession";
    public const string SmartScheme = "ArchForgeSmart";

    /// <summary>Подключает вход через браузер и локальную сессию (adr/0013). Работает только при настроенном <c>Authentication:Authority</c>
    /// (не в режиме разработки): иначе ничего не регистрирует. Вызывать после <c>AddArchForgeAuth</c>.</summary>
    public static IServiceCollection AddLocalSession(this IServiceCollection services, IConfiguration config)
    {
        var auth = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        if (auth.DevMode || string.IsNullOrWhiteSpace(auth.Authority)) return services;

        var opt = config.GetSection(LoginSessionOptions.Section).Get<LoginSessionOptions>() ?? new LoginSessionOptions();
        Validate(opt);
        services.Configure<LoginSessionOptions>(config.GetSection(LoginSessionOptions.Section));
        services.AddSingleton(auth);
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient<IOidcClient, OidcClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<SessionService>();
        services.AddHostedService<SessionRefreshService>();

        // Запрос с заголовком Authorization проверяется как токен (срез 1); запрос без него с этой же машины — по локальной сессии.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, LocalSessionHandler>(LocalScheme, null)
            .AddPolicyScheme(SmartScheme, SmartScheme, o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.ContainsKey("Authorization")
                    ? Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme
                    : LocalScheme);
        services.Configure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = SmartScheme; o.DefaultChallengeScheme = SmartScheme; });
        return services;
    }

    private static void Validate(LoginSessionOptions o)
    {
        if (o.GraceDays is < 1 or > 90) throw new InvalidOperationException("Auth:GraceDays — от 1 до 90.");
        if (o.WarnDays < 0 || o.WarnDays >= o.GraceDays) throw new InvalidOperationException("Auth:WarnDays должен быть меньше GraceDays.");
        if (string.IsNullOrWhiteSpace(o.ClientId) || o.ClientId.Length > 128) throw new InvalidOperationException("Auth:ClientId не задан.");
        if (!Uri.TryCreate(o.RedirectBaseUrl, UriKind.Absolute, out var r) || r.Scheme != Uri.UriSchemeHttp || !r.IsLoopback)
            throw new InvalidOperationException("Auth:RedirectBaseUrl — адрес http на этой машине (localhost, 127.0.0.1 или [::1]).");
        if (!Uri.TryCreate(o.FrontendUrl, UriKind.Absolute, out var f) || f.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Auth:FrontendUrl — абсолютный адрес страницы приложения.");
    }
}

/// <summary>Запросы с этой машины идут как вошедший пользователь, пока сессия пригодна (вошли, срок не вышел, часы целы). Запрос не с
/// loopback без токена остаётся анонимным.</summary>
internal sealed class LocalSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SessionService sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return AuthenticateResult.NoResult();
        if (await sessions.GetUsableAsync(Context.RequestAborted) is not { } s) return AuthenticateResult.NoResult();
        var claims = new List<Claim> { new(AuthClaims.Subject, s.Subject) };
        claims.AddRange(s.Roles.Select(r => new Claim(AuthClaims.Role, r)));
        claims.AddRange(s.Permissions.Select(p => new Claim(AuthClaims.Permissions, p)));
        var id = new ClaimsIdentity(claims, SessionRegistration.LocalScheme, AuthClaims.Subject, AuthClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), SessionRegistration.LocalScheme));
    }
}

/// <summary>Фоновое обновление токенов, когда сеть есть. Сбой одного круга не останавливает службу.</summary>
internal sealed class SessionRefreshService(SessionService sessions, ILogger<SessionRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try { await sessions.TickAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Не удалось обновить сессию в фоне."); }
        } while (await timer.WaitForNextTickAsync(ct).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result, CancellationToken.None));
    }
}
