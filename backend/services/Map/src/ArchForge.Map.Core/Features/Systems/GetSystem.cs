using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Systems;

/// <summary>
/// GET /api/system?path=&amp;drift= — системная карта (мульти-сервис, Фаза 1.5): берём системный IR у Analysis.
/// Map остаётся единственной фронт-фейсинг точкой (adr/0010) — линза «дрейф» доступна фронту только отсюда.
/// Проекции по сервису/уровню — позже (P1.5-G).
/// </summary>
public sealed class GetSystemEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system", async (string path, bool? drift, AnalysisClient analysis, CancellationToken ct) =>
        {
            GraphDto? graph = await analysis.GetSystemAsync(path, drift ?? false, ct);
            return graph is null
                ? Results.BadRequest(new { error = "Analysis не смог собрать систему" })
                : Results.Ok(graph);
        });
}
