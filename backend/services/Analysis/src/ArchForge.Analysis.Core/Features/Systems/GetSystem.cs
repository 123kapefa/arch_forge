using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ArchForge.Analysis.Core.Features.Systems;

// Вертикальный срез: разобрать КОРЕНЬ СИСТЕМЫ (мульти-сервис) → системный граф-контракт (Фаза 1.5).
// Один сервис на пути → тоже валидный системный граф (discovery найдёт 1).

// Линзы, требующие КОДА (тяжелее: разбор per-service): drift — «дрейф от свода» (convention);
// layers — «нарушение направления слоёв» (architecture). Обе opt-in, независимы (фронт включает нужную линзе).
// calls — «call-graph»: конкретные HTTP-вызовы между сервисами из кода (E3.1). Как drift/layers — opt-in, требует Roslyn.
// Profile (adr/0012): уровень + переопределения правил + пороги; null → профиль по умолчанию (Standard).
public sealed record GetSystemQuery(string Path, bool Drift = false, bool Layers = false, string? Env = null, bool Calls = false, ProjectProfile? Profile = null);

public sealed class GetSystemEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system", (string path, bool? drift, bool? layers, string? env, bool? calls,
            GetSystemHandler handler, ILogger<GetSystemEndpoint> log, CancellationToken ct) =>
        {
            try
            {
                GraphDto? graph = handler.Handle(new GetSystemQuery(path, drift ?? false, layers ?? false, env, calls ?? false), ct);
                return graph is null
                    ? Results.BadRequest(new { error = "path обязателен и должен существовать" })
                    : Results.Ok(graph);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // abort клиента дошёл до Roslyn-циклов — разбор прерван, не досчитываем впустую (видимость P1.5-H)
                log.LogInformation("Запрос /api/system отменён клиентом (abort) — разбор прерван: {Path}", path);
                throw; // ASP.NET завершит запрос как отменённый (клиент уже отключился)
            }
        });
}

public sealed class GetSystemHandler(ISystemSource source, ServiceAnalyzer? analyzer = null, IConstructionSource? construction = null)
{
    public GraphDto? Handle(GetSystemQuery query, CancellationToken ct = default)
    {
        // ISystemSource принимает и корень-папку, и путь к самому compose (как его тесты) — не сужаем.
        if (string.IsNullOrWhiteSpace(query.Path) ||
            !(Directory.Exists(query.Path) || File.Exists(query.Path))) return null;

        SystemGraph graph = source.Read(query.Path, query.Env, ct);
        var profile = query.Profile ?? ProjectProfile.Default;

        // Порог цикла сервисов — параметр профиля. Источник считает проблемы с порогом по умолчанию; если профиль
        // его изменил — пересчитываем только цикл (чистая функция над рёбрами графа), остальное не трогаем.
        if (profile.Thresholds.ServiceCycleMinConfidence != ServiceCycleDetector.DefaultMinConfidence)
            graph = graph with
            {
                Problems = [.. graph.Problems.Where(p => p.Kind != "service-cycle"),
                            .. ServiceCycleDetector.Detect(graph, profile.Thresholds.ServiceCycleMinConfidence)],
            };

        // Свод: из файла проекта (если есть), иначе встроенный. Нужен и для линз, и чтобы показать источник во фронте.
        var pack = Directory.Exists(query.Path) ? ConventionPack.Load(query.Path) : ConventionPack.Default();

        // Код-линзы (дрейф/слои) — по запросу, только для пути-папки, если анализатор доступен (в тестах может быть null).
        // Общий per-service разбор ОДИН раз → чистые детекторы по флагам + L0-сводка слоёв (без двойного Roslyn).
        IReadOnlyList<ServiceSummaryDto> services = [];
        IReadOnlyDictionary<string, MicroserviceLayerBuilder.Entry>? nodeLayers = null;
        List<string> degradations = []; // P1.5-H: что пропущено при разборе → «карта неполная» (видимо в контракте/UI)
        if ((query.Drift || query.Layers || query.Calls) && analyzer is not null && Directory.Exists(query.Path))
        {
            var analyses = analyzer.Analyze(query.Path, pack, ct);
            // сервис не разобрался (изоляция ошибок) → его типы/связи отсутствуют: честно помечаем деградацию
            foreach (var a in analyses.Failed())
                degradations.Add($"сервис «{a.Service.Name}» не разобрался — часть карты пропущена");
            var extra = new List<SystemProblem>();
            if (query.Drift) extra.AddRange(DriftDetector.Detect(analyses));
            if (query.Layers) extra.AddRange(LayerViolationDetector.Detect(analyses));
            // свод-линза «маппинг не централизован» — по оси конструирования (data-DTO собран инлайн в N мест)
            if (query.Drift && construction is not null)
            {
                var loc = analyses.Count > 0 ? analyses[0].Location : "";
                extra.AddRange(DtoMappingDetector.Detect(construction.Read(query.Path, ct), loc, pack.DtoMapping));
            }
            if (extra.Count > 0) graph = graph with { Problems = [.. graph.Problems, .. extra] };
            services = ServiceSummaryBuilder.Build(analyses);              // .sln-сводка (Services[])
            // compose-узлы сервисов → привязка кода вне /Services/ (gateway) к своему узлу по имени (#75)
            var composeServices = graph.Nodes
                .Where(n => n.Kind == SystemNodeKind.Service)
                .Select(n => (n.Id, n.Name)).ToList();
            nodeLayers = MicroserviceLayerBuilder.Build(analyses, composeServices); // per-microservice слои для L0-карточек

            // сквозной shared kernel — отдельный drill-таргет на L0 (типы под src/Shared, вне сервисов)
            if (SharedKernelBuilder.Build(analyses) is { } sk)
            {
                graph = graph with { Nodes = [.. graph.Nodes, sk.Node] };
                nodeLayers = new Dictionary<string, MicroserviceLayerBuilder.Entry>(nodeLayers, StringComparer.Ordinal)
                {
                    [MicroserviceLayerBuilder.NodeKey(sk.Node.Id, sk.Node.Name)] = sk.Entry,
                };
            }

            // call-graph (E3.1): конкретные http-вызовы из кода → на рёбра. ПОСЛЕ дедупа (граф из source уже сведён)
            // и после nodeLayers (нужны эндпоинты цели для RouteMatch). Дополняем рёбра, не заменяем (adr/0008).
            if (query.Calls)
                graph = HttpCallGraphBuilder.Enrich(graph, analyses, nodeLayers);
        }

        // Профиль: выключенные правила убираем, остальным ставим заданную строгость (после всех детекторов).
        graph = graph with { Problems = ProfileApplier.Apply(graph.Problems, profile) };

        string project = File.Exists(query.Path)
            ? Path.GetFileName(Path.GetDirectoryName(query.Path)) ?? "system"
            : new DirectoryInfo(query.Path.TrimEnd('/', '\\')).Name;
        var dto = graph.ToGraphDto(project, nodeLayers, pack) with
        {
            Services = services.Count > 0 ? services : null,
            Degradations = degradations.Count > 0 ? degradations : null,
        };
        return dto;
    }
}
