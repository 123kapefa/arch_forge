using ArchForge.Analysis.Contracts;
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
    public static IResult ToResult(Outcome<ConfigManifestDto> o) =>
        o.Ok
            ? (o.Status == 201 ? Results.Created($"/api/profile/configs/{o.Value!.Id}", o.Value) : Results.Ok(o.Value))
            : Fail(o.Status, o.Errors);

    public static IResult ToResult<T>(Outcome<T> o) =>
        o.Ok ? Results.Ok(o.Value) : Fail(o.Status, o.Errors);

    public static IResult Fail(int status, IReadOnlyList<string> errors) =>
        Results.Json(new { error = errors.FirstOrDefault() ?? "ошибка", errors }, statusCode: status);

    /// <summary>Сбои хранилища (повреждённый файл, нет прав на запись) — понятный ответ, а не голый 500.</summary>
    public static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (InvalidDataException e) { return Fail(503, [e.Message]); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Fail(503, ["Хранилище профилей недоступно для записи или чтения: " + e.Message]); }
    }
}

/// <summary>GET /api/rules — каталог правил анализатора (id, категория, строгость по умолчанию на каждом уровне).</summary>
public sealed class GetRulesEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/rules", async (IRuleCatalogProvider catalog, CancellationToken ct) =>
            await catalog.GetAsync(ct) is { } rules
                ? Results.Ok(rules)
                : ProfileHttp.Fail(503, ["Analysis не отвечает"]));
}

/// <summary>GET /api/vocabulary — встроенный словарь ролей, имена сервисов и пороги: точка отсчёта для отличий профиля.</summary>
public sealed class GetVocabularyEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/vocabulary", async (AnalysisClient analysis, CancellationToken ct) =>
            await analysis.GetVocabularyAsync(ct) is { } v
                ? Results.Ok(v)
                : ProfileHttp.Fail(503, ["Analysis не отвечает"]));
}

/// <summary>POST /api/profile/preview?path= — предпросмотр влияния профиля (тело — ProfileDto): находки до и после,
/// совет уровня, счётчики словаря. Ничего не сохраняет. Профиль проверяется так же, как при сохранении.</summary>
public sealed class PreviewProfileEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/api/profile/preview", (string path, ProfileDto? profile, AnalysisClient analysis, IRuleCatalogProvider catalog, CancellationToken ct) =>
            ProfileHttp.Guard(async () =>
            {
                if (string.IsNullOrWhiteSpace(path) || path.Length > ProfileService.MaxPathLength || path.Contains('\0'))
                    return ProfileHttp.Fail(400, ["path обязателен"]);
                if (profile is null) return ProfileHttp.Fail(400, ["тело запроса обязательно"]);
                var rules = await catalog.GetAsync(ct);
                if (rules is null) return ProfileHttp.Fail(503, ["Analysis не отвечает"]);
                var errors = ProfileValidator.Validate(profile, rules.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal));
                if (errors.Count > 0) return ProfileHttp.Fail(400, [.. errors]);
                return await analysis.PreviewImpactAsync(path, profile, ct) is { } dto
                    ? Results.Ok(dto)
                    : ProfileHttp.Fail(502, ["Analysis не смог посчитать предпросмотр: сервис недоступен, не принял путь или профиль, либо разбор не уложился во время"]);
            }));
}

/// <summary>Список и чтение конфигураций: встроенные уровни + свои.</summary>
public sealed class ListConfigsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profile/configs", (ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () => Results.Ok(await svc.ListAsync(ct))));
        app.MapGet("/api/profile/configs/{id}", (string id, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () => await svc.GetAsync(id, ct) is { } c ? Results.Ok(c) : ProfileHttp.Fail(404, ["конфигурация не найдена"])));
    }
}

/// <summary>Создание, изменение, удаление и копирование своих конфигураций.</summary>
public sealed class ManageConfigsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/profile/configs", (ConfigManifestDto? config, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () => config is null
                ? ProfileHttp.Fail(400, ["тело запроса обязательно"])
                : ProfileHttp.ToResult(await svc.CreateAsync(config, ct))));

        app.MapPut("/api/profile/configs/{id}", (string id, ConfigManifestDto? config, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () => config is null
                ? ProfileHttp.Fail(400, ["тело запроса обязательно"])
                : ProfileHttp.ToResult(await svc.UpdateAsync(id, config, ct))));

        app.MapDelete("/api/profile/configs/{id}", (string id, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () =>
            {
                var o = await svc.DeleteAsync(id, ct);
                return o.Ok ? Results.NoContent() : ProfileHttp.ToResult(o);
            }));

        app.MapPost("/api/profile/configs/{id}/duplicate", (string id, string? author, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () => ProfileHttp.ToResult(await svc.DuplicateAsync(id, author, ct))));
    }
}

/// <summary>Привязка проекта к конфигурации: GET /api/profile?path= и PUT /api/profile?path= (тело — ProfileBindingDto).</summary>
public sealed class ProjectProfileEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profile", (string path, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () => ProfileHttp.ToResult(await svc.GetForPathAsync(path, ct))));

        app.MapPut("/api/profile", (string path, ProfileBindingDto? binding, ProfileService svc, CancellationToken ct) =>
            ProfileHttp.Guard(async () => ProfileHttp.ToResult(await svc.BindProjectAsync(path, binding, ct))));
    }
}
