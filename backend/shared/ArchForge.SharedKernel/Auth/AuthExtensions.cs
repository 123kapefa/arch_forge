using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ArchForge.SharedKernel.Auth;

public static class AuthExtensions
{
    public const string UserPolicy = "user";

    /// <summary>Подключает проверку входа (adr/0013). Есть ровно три исхода: настоящий вход (Authority), режим разработки
    /// (явный флаг, только Development) или отказ запуститься. Молча «без защиты» приложение не стартует.</summary>
    public static IServiceCollection AddArchForgeAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var o = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        var hasAuthority = !string.IsNullOrWhiteSpace(o.Authority);

        if (o.DevMode)
        {
            if (hasAuthority) throw new InvalidOperationException("Authentication:DevMode нельзя включать вместе с Authentication:Authority: режим разработки заменяет вход, а не дополняет его.");
            if (!env.IsDevelopment()) throw new InvalidOperationException("Authentication:DevMode разрешён только в окружении Development.");
            services.AddAuthentication(DevIdentity.Scheme).AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevIdentity.Scheme, null);
        }
        else if (hasAuthority)
        {
            if (string.IsNullOrWhiteSpace(o.Audience)) throw new InvalidOperationException("Authentication:Audience обязателен: токен должен быть адресован этому приложению.");
            if (!Uri.TryCreate(o.Authority, UriKind.Absolute, out var authority)) throw new InvalidOperationException("Authentication:Authority — не адрес.");
            var insecureAllowed = o.AllowInsecureMetadata && env.IsDevelopment();
            if (authority.Scheme != Uri.UriSchemeHttps && !insecureAllowed)
                throw new InvalidOperationException("Authentication:Authority должен быть https (http допустим только с AllowInsecureMetadata в Development).");
            if (!string.IsNullOrWhiteSpace(o.InternalAuthority)
                && (!Uri.TryCreate(o.InternalAuthority, UriKind.Absolute, out var inner) || (inner.Scheme != Uri.UriSchemeHttps && !insecureAllowed)))
                throw new InvalidOperationException("Authentication:InternalAuthority должен быть https-адресом (http допустим только с AllowInsecureMetadata в Development).");
            var meta = string.IsNullOrWhiteSpace(o.InternalAuthority) ? null : o.InternalAuthority!.TrimEnd('/') + "/.well-known/openid-configuration";

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(b =>
            {
                b.Authority = o.Authority;
                if (meta is not null) b.MetadataAddress = meta;
                b.RequireHttpsMetadata = !insecureAllowed;
                b.IncludeErrorDetails = env.IsDevelopment();   // причину отказа в заголовок ответа — только при разработке
                b.MapInboundClaims = false;   // claims остаются как в токене (sub, role, permissions)
                b.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = o.Authority.TrimEnd('/'),   // Keycloak пишет iss без завершающего «/»
                    ValidateAudience = true,
                    ValidAudience = o.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),   // часы пользовательской машины идут не точно
                    NameClaimType = AuthClaims.Subject,
                    RoleClaimType = AuthClaims.Role,
                };
            });
        }
        else
        {
            throw new InvalidOperationException("Вход не настроен. Задайте Authentication:Authority и Authentication:Audience, а для локальной разработки (окружение Development) — Authentication:DevMode=true.");
        }

        services.AddSingleton(new AuthMode(o.DevMode));
        services.AddAuthorization(a =>
        {
            a.AddPolicy(UserPolicy, p => p.RequireAuthenticatedUser());
            if (o.RequireLogin) a.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();   // всё API — только для вошедших
        });
        return services;
    }

    public static IApplicationBuilder UseArchForgeAuth(this IApplicationBuilder app) => app.UseAuthentication().UseAuthorization();

    /// <summary>Нужны ВСЕ перечисленные права из токена (claim <c>permissions</c>). Без входа — 401, без права — 403.</summary>
    public static TBuilder RequirePermissions<TBuilder>(this TBuilder builder, params string[] permissions) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => permissions.All(p => ctx.User.FindAll(AuthClaims.Permissions).Any(c => c.Value == p)))
            .Build());

    // ── закрытый канал Map → Analysis ──

    public const string InternalKeyHeader = "X-ArchForge-Internal-Key";

    /// <summary>Ключ канала Map → Analysis: печатные ASCII-символы, 16–256 (иначе он не станет заголовком HTTP или слишком слаб).</summary>
    public static bool IsValidInternalKey(string? key) =>
        key is { Length: >= 16 and <= 256 } && key.All(c => c is >= '!' and <= '~');

    /// <summary>Проверка настройки ключа на старте. Вне Development ключ обязателен у обоих сервисов; в Development можно без него
    /// (тогда Analysis принимает только loopback). Заданный ключ должен быть допустимого вида.</summary>
    public static string? ValidateInternalKey(IConfiguration config, IHostEnvironment env)
    {
        var key = config["Internal:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!env.IsDevelopment()) throw new InvalidOperationException("Internal:Key обязателен вне Development: Map и Analysis должны использовать один общий ключ.");
            return null;
        }
        if (!IsValidInternalKey(key)) throw new InvalidOperationException("Internal:Key: от 16 до 256 печатных ASCII-символов без пробелов.");
        return key;
    }

    /// <summary>Закрывает внутренний сервис (Analysis): запросы только с loopback и, если задан <c>Internal:Key</c>, с этим ключом
    /// в заголовке. Без ключа это допустимо только в Development; в остальных окружениях сервис не стартует.</summary>
    public static IApplicationBuilder UseInternalGuard(this WebApplication app)
    {
        var key = ValidateInternalKey(app.Configuration, app.Environment);
        var keyed = key is not null;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InternalGuard");
        if (!keyed) log.LogWarning("Internal:Key не задан — сервис принимает вызовы только с loopback (допустимо в Development).");
        var expected = keyed ? SHA256.HashData(Encoding.UTF8.GetBytes(key!)) : null;

        return app.Use(async (ctx, next) =>
        {
            var ok = ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
            if (ok && expected is not null)
            {
                var given = SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Headers[InternalKeyHeader].ToString()));
                ok = CryptographicOperations.FixedTimeEquals(given, expected);   // сравнение за постоянное время
            }
            if (!ok)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "внутренний сервис: доступ только для Map" });
                return;
            }
            await next();
        });
    }
}

/// <summary>Режим разработки: любой запрос с loopback получает личность <c>dev</c> с ограниченными правами. Запрос не с loopback
/// остаётся анонимным. Регистрируется только при <c>DevMode</c> в Development (см. <see cref="AuthExtensions.AddArchForgeAuth"/>).</summary>
internal sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(AuthClaims.Subject, DevIdentity.Subject), new(AuthClaims.Role, "dev") };
        claims.AddRange(DevIdentity.Permissions.Select(p => new Claim(AuthClaims.Permissions, p)));
        var id = new ClaimsIdentity(claims, DevIdentity.Scheme, AuthClaims.Subject, AuthClaims.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), DevIdentity.Scheme)));
    }
}
