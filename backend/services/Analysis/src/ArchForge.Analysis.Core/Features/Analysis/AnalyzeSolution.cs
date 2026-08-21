using ArchForge.Analysis.Domain;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Analysis.Core.Features.Analysis;

// Вертикальный срез: разобрать .NET-сервис по пути → граф-контракт (роли, дрейф, покрытие, рёбра).

public sealed record AnalyzeSolutionQuery(string Path);

public sealed class AnalyzeSolutionEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/analysis", (string path, AnalyzeSolutionHandler handler, CancellationToken ct) =>
        {
            // ct = HttpContext.RequestAborted: разрыв соединения клиентом останавливает разбор (P1-H).
            GraphDto? graph = handler.Handle(new AnalyzeSolutionQuery(path), ct);
            return graph is null
                ? Results.BadRequest(new { error = "path обязателен и должен существовать" })
                : Results.Ok(graph);
        });
}

public sealed class AnalyzeSolutionHandler(ITypeSource source, TypeClassifier classifier)
{
    public GraphDto? Handle(AnalyzeSolutionQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Path) || !Directory.Exists(query.Path)) return null;

        var pack = ConventionPack.Load(query.Path);
        AnalysisResult result = classifier.Classify(source.Read(query.Path, ct), pack);
        string project = new DirectoryInfo(query.Path.TrimEnd('/', '\\')).Name;
        return result.ToGraphDto(project);
    }
}
