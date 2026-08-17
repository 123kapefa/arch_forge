using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Analysis.Core.Features.Systems;

// Вертикальный срез: разобрать КОРЕНЬ СИСТЕМЫ (мульти-сервис) → системный граф-контракт (Фаза 1.5).
// Один сервис на пути → тоже валидный системный граф (discovery найдёт 1).

// Линзы, требующие КОДА (тяжелее: разбор per-service): drift — «дрейф от свода» (convention);
// layers — «нарушение направления слоёв» (architecture). Обе opt-in, независимы (фронт включает нужную линзе).
public sealed record GetSystemQuery(string Path, bool Drift = false, bool Layers = false);

public sealed class GetSystemEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system", (string path, bool? drift, bool? layers, GetSystemHandler handler, CancellationToken ct) =>
        {
            GraphDto? graph = handler.Handle(new GetSystemQuery(path, drift ?? false, layers ?? false), ct);
            return graph is null
                ? Results.BadRequest(new { error = "path обязателен и должен существовать" })
                : Results.Ok(graph);
        });
}

public sealed class GetSystemHandler(ISystemSource source, ServiceAnalyzer? analyzer = null)
{
    public GraphDto? Handle(GetSystemQuery query, CancellationToken ct = default)
    {
        // ISystemSource принимает и корень-папку, и путь к самому compose (как его тесты) — не сужаем.
        if (string.IsNullOrWhiteSpace(query.Path) ||
            !(Directory.Exists(query.Path) || File.Exists(query.Path))) return null;

        SystemGraph graph = source.Read(query.Path, ct);

        // Свод: из файла проекта (если есть), иначе встроенный. Нужен и для линз, и чтобы показать источник во фронте.
        var pack = Directory.Exists(query.Path) ? ConventionPack.Load(query.Path) : ConventionPack.Default();

        // Код-линзы (дрейф/слои) — по запросу, только для пути-папки, если анализатор доступен (в тестах может быть null).
        // Общий per-service разбор ОДИН раз → чистые детекторы по флагам + L0-сводка слоёв (без двойного Roslyn).
        IReadOnlyList<ServiceSummaryDto> services = [];
        IReadOnlyDictionary<string, MicroserviceLayerBuilder.Entry>? nodeLayers = null;
        if ((query.Drift || query.Layers) && analyzer is not null && Directory.Exists(query.Path))
        {
            var analyses = analyzer.Analyze(query.Path, pack, ct);
            var extra = new List<SystemProblem>();
            if (query.Drift) extra.AddRange(DriftDetector.Detect(analyses));
            if (query.Layers) extra.AddRange(LayerViolationDetector.Detect(analyses));
            if (extra.Count > 0) graph = graph with { Problems = [.. graph.Problems, .. extra] };
            services = ServiceSummaryBuilder.Build(analyses);              // .sln-сводка (Services[])
            nodeLayers = MicroserviceLayerBuilder.Build(analyses);        // per-microservice слои для L0-карточек

            // сквозной shared kernel — отдельный drill-таргет на L0 (типы под src/Shared, вне сервисов)
            if (SharedKernelBuilder.Build(analyses) is { } sk)
            {
                graph = graph with { Nodes = [.. graph.Nodes, sk.Node] };
                nodeLayers = new Dictionary<string, MicroserviceLayerBuilder.Entry>(nodeLayers, StringComparer.Ordinal)
                {
                    [MicroserviceLayerBuilder.NodeKey(sk.Node.Id, sk.Node.Name)] = sk.Entry,
                };
            }
        }

        string project = File.Exists(query.Path)
            ? Path.GetFileName(Path.GetDirectoryName(query.Path)) ?? "system"
            : new DirectoryInfo(query.Path.TrimEnd('/', '\\')).Name;
        var dto = SystemGraphMapper.ToDto(graph, project, nodeLayers, pack);
        return services.Count > 0 ? dto with { Services = services } : dto;
    }
}
