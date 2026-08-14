using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Imports;

// GET /api/imports?path= — импорты для фронта: проксируем Analysis (проекций нет).
public sealed class GetImportsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/imports", async (string path, AnalysisClient analysis, CancellationToken ct) =>
        {
            ImportsDto? imports = await analysis.GetImportsAsync(path, ct);
            return imports is null
                ? Results.BadRequest(new { error = "Analysis не смог прочитать импорты" })
                : Results.Ok(imports);
        });
}
