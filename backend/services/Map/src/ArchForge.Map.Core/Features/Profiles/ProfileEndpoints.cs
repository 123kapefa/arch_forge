using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Profiles;

// Профили проекта и конфигурации анализа (adr/0012). Map — единственная фронт-фейсинг точка (adr/0010):
// фронт ходит сюда, а не в Analysis. Эндпоинты тонкие — логика в ProfileService.

internal static class ProfileHttp
{
    public static IResult ToResult<T>(Outcome<T> o, string? createdAt = null) =>
        o.Ok
            ? (o.Status == 201 && createdAt is not null ? Results.Created(createdAt, o.Value) : Results.Ok(o.Value))
            : Results.Json(new { error = o.Errors.FirstOrDefault() ?? "ошибка", errors = o.Errors }, statusCode: o.Status);
}

/// <summary>GET /api/rules — каталог правил анализатора (id, категория, строгость по умолчанию на каждом уровне).</summary>
public sealed class GetRulesEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/rules", async (IRuleCatalogProvider catalog, CancellationToken ct) =>
            await catalog.GetAsync(ct) is { } rules
                ? Results.Ok(rules)
                : Results.Json(new { error = "Analysis не отвечает" }, statusCode: 503));
}

/// <summary>Список и чтение конфигураций: встроенные уровни + свои.</summary>
public sealed class ListConfigsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profile/configs", async (ProfileService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        app.MapGet("/api/profile/configs/{id}", async (string id, ProfileService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound(new { error = "конфигурация не найдена" }));
    }
}

/// <summary>Создание, изменение, удаление и копирование своих конфигураций.</summary>
public sealed class ManageConfigsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/profile/configs", async (ConfigManifestDto? config, ProfileService svc, CancellationToken ct) =>
            config is null
                ? Results.BadRequest(new { error = "тело запроса обязательно" })
                : ProfileHttp.ToResult(await svc.CreateAsync(config, ct), "/api/profile/configs"));

        app.MapPut("/api/profile/configs/{id}", async (string id, ConfigManifestDto? config, ProfileService svc, CancellationToken ct) =>
            config is null
                ? Results.BadRequest(new { error = "тело запроса обязательно" })
                : ProfileHttp.ToResult(await svc.UpdateAsync(id, config, ct)));

        app.MapDelete("/api/profile/configs/{id}", async (string id, ProfileService svc, CancellationToken ct) =>
        {
            var o = await svc.DeleteAsync(id, ct);
            return o.Ok ? Results.NoContent() : ProfileHttp.ToResult(o);
        });

        app.MapPost("/api/profile/configs/{id}/duplicate", async (string id, string? author, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.ToResult(await svc.DuplicateAsync(id, author, ct), "/api/profile/configs"));
    }
}

/// <summary>Привязка проекта к конфигурации: GET /api/profile?path= и PUT /api/profile?path= (тело — ProfileBindingDto).</summary>
public sealed class ProjectProfileEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profile", async (string path, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.ToResult(await svc.GetForPathAsync(path, ct)));

        app.MapPut("/api/profile", async (string path, ProfileBindingDto? binding, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.ToResult(await svc.BindProjectAsync(path, binding, ct)));
    }
}
