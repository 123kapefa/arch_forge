using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Analysis.Core.Features.Imports;

// Вертикальный срез: импорты (библиотеки) сервиса по пути → ImportsDto.

public sealed record GetImportsQuery(string Path);

public sealed class GetImportsEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/imports", (string path, GetImportsHandler handler) =>
        {
            ImportsDto? imports = handler.Handle(new GetImportsQuery(path));
            return imports is null
                ? Results.BadRequest(new { error = "path обязателен и должен существовать" })
                : Results.Ok(imports);
        });
}

public sealed class GetImportsHandler(IImportSource source)
{
    public ImportsDto? Handle(GetImportsQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Path) || !Directory.Exists(query.Path)) return null;
        string project = new DirectoryInfo(query.Path.TrimEnd('/', '\\')).Name;
        return ImportMapper.ToDto(source.Read(query.Path), project);
    }
}
