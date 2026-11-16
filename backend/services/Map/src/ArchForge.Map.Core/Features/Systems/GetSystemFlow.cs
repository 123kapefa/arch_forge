using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Systems;

/// <summary>
/// GET /api/system/flow?path=&amp;entry=&amp;endpoint=&amp;env=&amp;maxDepth=&amp;maxSteps= — поток (E3.4): цепочка вызовов от сервиса-входа
/// по call-graph (линза вызовов включается сама). Новый эндпоинт — контракт /api/system не менялся.
/// </summary>
public sealed class GetSystemFlowEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system/flow", async (string path, string entry, string? endpoint, string? env, int? maxDepth, int? maxSteps,
            AnalysisClient analysis, ProfileService profiles, CancellationToken ct) =>
        {
            GraphDto? graph = await GetSystemEndpoint.LoadAsync(analysis, profiles, path, drift: false, layers: false, env, calls: true, refresh: false, ct);
            if (graph is null) return Results.BadRequest(new { error = "Analysis не смог собрать систему" });
            FlowDto? flow = FlowChainBuilder.Build(graph, entry, endpoint, maxDepth, maxSteps);
            return flow is null
                ? Results.NotFound(new { error = "входной сервис не найден в системе" })
                : Results.Ok(flow);
        });
}
