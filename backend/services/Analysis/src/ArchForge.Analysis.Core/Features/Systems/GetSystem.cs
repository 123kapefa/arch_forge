using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Analysis.Core.Features.Systems;

// Вертикальный срез: разобрать КОРЕНЬ СИСТЕМЫ (мульти-сервис) → системный граф-контракт (Фаза 1.5).
// Один сервис на пути → тоже валидный системный граф (discovery найдёт 1).

public sealed record GetSystemQuery(string Path);

public sealed class GetSystemEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system", (string path, GetSystemHandler handler, CancellationToken ct) =>
        {
            GraphDto? graph = handler.Handle(new GetSystemQuery(path), ct);
            return graph is null
                ? Results.BadRequest(new { error = "path обязателен и должен существовать" })
                : Results.Ok(graph);
        });
}

public sealed class GetSystemHandler(ISystemSource source)
{
    public GraphDto? Handle(GetSystemQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Path) || !Directory.Exists(query.Path)) return null;

        SystemGraph graph = source.Read(query.Path, ct);
        string project = new DirectoryInfo(query.Path.TrimEnd('/', '\\')).Name;
        return SystemGraphMapper.ToDto(graph, project);
    }
}
