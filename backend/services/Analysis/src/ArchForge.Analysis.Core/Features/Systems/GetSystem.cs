using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Analysis.Core.Features.Systems;

// Вертикальный срез: разобрать КОРЕНЬ СИСТЕМЫ (мульти-сервис) → системный граф-контракт (Фаза 1.5).
// Один сервис на пути → тоже валидный системный граф (discovery найдёт 1).

// drift=true включает линзу «дрейф от свода» — доп. проблемы категории convention (тяжелее: разбор кода per-service).
public sealed record GetSystemQuery(string Path, bool Drift = false);

public sealed class GetSystemEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system", (string path, bool? drift, GetSystemHandler handler, CancellationToken ct) =>
        {
            GraphDto? graph = handler.Handle(new GetSystemQuery(path, drift ?? false), ct);
            return graph is null
                ? Results.BadRequest(new { error = "path обязателен и должен существовать" })
                : Results.Ok(graph);
        });
}

public sealed class GetSystemHandler(ISystemSource source, DriftScanner? drift = null)
{
    public GraphDto? Handle(GetSystemQuery query, CancellationToken ct = default)
    {
        // ISystemSource принимает и корень-папку, и путь к самому compose (как его тесты) — не сужаем.
        if (string.IsNullOrWhiteSpace(query.Path) ||
            !(Directory.Exists(query.Path) || File.Exists(query.Path))) return null;

        SystemGraph graph = source.Read(query.Path, ct);

        // Линза «дрейф» — только по запросу и только если сканер доступен (в тестах может быть null).
        if (query.Drift && drift is not null && Directory.Exists(query.Path))
        {
            var convention = drift.Scan(query.Path, ct);
            if (convention.Count > 0) graph = graph with { Problems = [.. graph.Problems, .. convention] };
        }

        string project = File.Exists(query.Path)
            ? Path.GetFileName(Path.GetDirectoryName(query.Path)) ?? "system"
            : new DirectoryInfo(query.Path.TrimEnd('/', '\\')).Name;
        return SystemGraphMapper.ToDto(graph, project);
    }
}
