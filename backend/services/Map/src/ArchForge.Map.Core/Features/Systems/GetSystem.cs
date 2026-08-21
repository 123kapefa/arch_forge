using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Systems;

/// <summary>
/// GET /api/system?path=&amp;drift=&amp;layers= — системная карта (мульти-сервис, Фаза 1.5): системный IR у Analysis.
/// Map — единственная фронт-фейсинг точка (adr/0010): код-линзы (дрейф/слои) доступны фронту только отсюда.
/// Проекции по сервису/уровню — позже (P1.5-G).
/// </summary>
public sealed class GetSystemEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system", async (string path, bool? drift, bool? layers, string? env, AnalysisClient analysis, CancellationToken ct) =>
        {
            GraphDto? graph = await analysis.GetSystemAsync(path, drift ?? false, layers ?? false, env, ct);
            return graph is null
                ? Results.BadRequest(new { error = "Analysis не смог собрать систему" })
                : Results.Ok(graph);
        });
}
