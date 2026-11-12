using ArchForge.SharedKernel;
using ArchForge.SharedKernel.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Auth;

/// <summary>GET /api/me — кто вошёл (adr/0013). Только сведения из проверенного токена; сами токены наружу не отдаются.
/// Без входа — 401. В режиме разработки отвечает личностью dev и говорит об этом.</summary>
public sealed class MeEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/me", (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromServices] AuthMode mode) => Results.Ok(new MeDto(
            ctx.User.FindFirst(AuthClaims.Subject)?.Value ?? "",
            [.. ctx.User.FindAll(AuthClaims.Role).Select(c => c.Value)],
            [.. ctx.User.FindAll(AuthClaims.Permissions).Select(c => c.Value)],
            mode.DevMode))).RequireAuthorization(AuthExtensions.UserPolicy);
}

public sealed record MeDto(string Subject, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, bool DevMode);
