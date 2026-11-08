using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ArchForge.Map.Core;

/// <summary>Защита локального API от чужих страниц в браузере пользователя (CSRF). Изменяющие запросы с заголовком
/// <c>Sec-Fetch-Site: cross-site</c> (их ставит браузер, страница подменить его не может) отклоняются с 403.
/// Страница фронта с другого порта той же машины (same-site) и запросы без заголовка (curl, тесты, другие сервисы) допускаются.
/// От DNS rebinding защищает <c>AllowedHosts</c> в appsettings.</summary>
public static class CrossSiteGuard
{
    public static IApplicationBuilder UseCrossSiteGuard(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var m = ctx.Request.Method;
            var isUnsafe = !HttpMethods.IsGet(m) && !HttpMethods.IsHead(m) && !HttpMethods.IsOptions(m);
            if (isUnsafe && ctx.Request.Headers["Sec-Fetch-Site"].ToString().Equals("cross-site", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "запрос с другого сайта отклонён" });
                return;
            }
            await next();
        });
}
