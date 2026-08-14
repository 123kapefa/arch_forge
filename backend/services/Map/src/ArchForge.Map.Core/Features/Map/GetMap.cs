using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Map;

/// <summary>
/// GET /api/map?path=&amp;focus=&amp;layer= — карта для фронта:
/// берём IR у Analysis и применяем проекции (слой → focus).
/// </summary>
public sealed class GetMapEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/map", async (
            string path,
            string? focus,
            string? layer,
            AnalysisClient analysis,
            CancellationToken ct) =>
        {
            GraphDto? graph = await analysis.AnalyzeAsync(path, ct);
            if (graph is null)
                return Results.BadRequest(new { error = "Analysis не смог разобрать path" });

            if (!string.IsNullOrWhiteSpace(layer))
                graph = GraphProjections.OnlyLayer(graph, layer);
            if (!string.IsNullOrWhiteSpace(focus))
                graph = GraphProjections.Focus(graph, focus);

            return Results.Ok(graph);
        });
}
