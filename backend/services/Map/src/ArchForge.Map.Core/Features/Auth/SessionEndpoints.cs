using System.Net;
using ArchForge.Map.Core.Auth;
using ArchForge.SharedKernel;
using ArchForge.SharedKernel.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace ArchForge.Map.Core.Features.Auth;

/// <summary>Вход и состояние сессии (adr/0013). Работают только для запросов с этой машины: сведения о локальном пользователе
/// и управление его сессией не для сети. Токены наружу не отдаются.</summary>
public sealed class SessionEndpoints : IEndpoint
{
    private const string CallbackPath = "/api/auth/callback";

    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/session", async (HttpContext ctx, [FromServices] AuthMode mode, [FromServices] SessionService? sessions, CancellationToken ct) =>
        {
            if (!Local(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (mode.DevMode) return Results.Ok(new SessionStatusDto("dev", DevIdentity.Subject, null, null, false, "none"));
            if (sessions is null) return Results.Ok(new SessionStatusDto("none", null, null, null, false, "none"));
            return Results.Ok(await sessions.GetStatusAsync(ct));
        }).AllowAnonymous();

        app.MapPost("/api/auth/login", async (HttpContext ctx, [FromServices] SessionService? sessions, [FromServices] IOptions<LoginSessionOptions>? opt, CancellationToken ct) =>
        {
            if (!Local(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (sessions is null || opt is null) return Results.BadRequest(new { error = "вход не настроен (режим разработки или нет Authentication:Authority)" });
            try
            {
                var url = await sessions.BeginLoginAsync(opt.Value.RedirectBaseUrl.TrimEnd('/') + CallbackPath, ct);
                return Results.Ok(new { url = url.ToString() });
            }
            catch (OidcException e) { return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException e) { return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status429TooManyRequests); }
        }).AllowAnonymous();

        // Сюда браузер возвращается с сервера входа. Адрес возврата фиксирован настройкой (не берётся из запроса).
        app.MapGet(CallbackPath, async (HttpContext ctx, string? code, string? state, string? error, [FromServices] SessionService? sessions,
            [FromServices] IOptions<LoginSessionOptions>? opt, CancellationToken ct) =>
        {
            if (!Local(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (sessions is null || opt is null) return Results.BadRequest(new { error = "вход не настроен" });
            var back = opt.Value.FrontendUrl;
            var sep = back.Contains('?') ? '&' : '?';
            if (error is not null || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || code.Length > 2048 || state.Length > 256)
                return Results.Redirect(back + sep + "login=failed");
            try { await sessions.CompleteLoginAsync(state, code, ct); }
            catch (OidcException) { return Results.Redirect(back + sep + "login=failed"); }
            return Results.Redirect(back + sep + "login=ok");
        }).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext ctx, [FromServices] SessionService? sessions, CancellationToken ct) =>
        {
            if (!Local(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (sessions is not null) await sessions.LogoutAsync(ct);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    private static bool Local(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
}
