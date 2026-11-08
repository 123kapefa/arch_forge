using ArchForge.Analysis.Core.Profiles;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Analysis.Core.Features.Rules;

/// <summary>GET /api/rules — каталог правил анализатора (adr/0012): что проверяется и как строго по умолчанию на каждом уровне.
/// Нужен Map и интерфейсу настройки; значения не зависят от проекта.</summary>
public sealed class GetRulesEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/rules", () => Results.Ok(ProfileMapping.ToCatalogDto()));
}

/// <summary>GET /api/vocabulary — встроенные значения словаря ролей, имён сервисов и порогов (adr/0012):
/// точка отсчёта, от которой интерфейс показывает отличия профиля.</summary>
public sealed class GetVocabularyEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/vocabulary", () => Results.Ok(ProfileMapping.ToVocabularyDefaults()));
}
