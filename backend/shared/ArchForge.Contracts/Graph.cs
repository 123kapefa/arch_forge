using System.Text.Json.Serialization;

namespace ArchForge.Contracts;

// Язык-агностичный контракт графа архитектуры (IR). Общий для Analysis, Map и фронта.
// Форма ориентирована на вьюер: у узлов Id, у рёбер Source/Target (как ждёт React Flow).
//
// Фаза 1.5 расширяет контракт СТРОГО АДДИТИВНО: только новые ОПЦИОНАЛЬНЫЕ поля (nullable, по умолчанию
// null, с [JsonIgnore WhenWritingNull]) и новые ЗНАЧЕНИЯ строковых полей kind/via. Продьюсеры Фазы 1
// не меняются (новые параметры получают дефолты), а их JSON — байт-совместим (null-поля не пишутся),
// поэтому клиенты Фазы 1 работают как раньше (api-contracts, adr).

public sealed record GraphDto(
    string Project,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<EdgeDto> Edges,
    CoverageDto Coverage,
    IReadOnlyList<string> Drift,
    // --- Фаза 1.5: системный уровень ---
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Health = null,                                 // ok | warning | critical (сводно по системе)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ProblemDto>? Problems = null,            // проблемы этого scope (сортируются по важности)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ServiceSummaryDto>? Services = null,     // L0-сводка по найденным сервисам (.sln) со слоями; при код-линзах
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EnvironmentDto>? Environments = null,    // зарезервировано под P1.5; в Фазе 1.5 не заполняется
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ConventionsDto? Conventions = null,                    // источник свода (встроенный / файл) + счётчики
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Degradations = null);           // P1.5-H: что урезано/пропущено (сбой сервиса…) — «карта неполная»

public sealed record NodeDto(
    string Id,
    string Name,
    string Kind,   // Ф1: class|record|struct|interface|enum · Ф1.5(+): service|datastore|topic|member|external
    string Layer,
    string Role,
    string Via,    // marker | heuristic | unknown
    string File,
    IReadOnlyList<string> Drift,
    // --- Фаза 1.5 ---
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Service = null,                                // id сервиса-владельца
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Parent = null,                                 // id родителя (member→тип, тип→слой/сервис)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Level = null,                                  // system | service | layer | type | member
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Health = null,                                 // ok | warning | critical
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<MemberDto>? Members = null,              // члены type-узла (эндпоинты контроллера и т.п.)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LayerSummaryDto>? Layers = null,        // слои сервиса-узла (L0: «внутри сервиса — слои»)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Root = null,                                   // папка микросервиса (drill L1: /api/map по нему)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<EndpointDto>? Endpoints = null,          // HTTP-эндпоинты сервиса (L1-список + счётчик L0; #71)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DeployDto? Deploy = null);                             // DevOps-атрибуты из compose (E9): порты/healthcheck/сети — вид Deploy (#89)

public sealed record EdgeDto(
    string Id,
    string Source, // Node.Id
    string Target, // Node.Id
    string Kind,   // Ф1: implements|holds|uses|raises|handles · Ф1.5(+): http-call|rw-db|uses-bus|publishes|subscribes|calls
    // --- Фаза 1.5 ---
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Via = null,                                    // compose | config | code | unknown (провенанс)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Confidence = null,                             // 0..1 (уверенность по источнику)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Violation = null,                                // связь-нарушение (подсветка)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Problem = null);                               // id проблемы (для навигации)

public sealed record CoverageDto(int Marker, int Heuristic, int Unknown, int Total);

// --- Фаза 1.5: новые записи ---

/// <summary>Член типа: эндпоинт / метод / свойство / поле.</summary>
public sealed record MemberDto(
    string Id,
    string Name,
    string Kind,   // endpoint | method | property | field
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Signature = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HttpMethod = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Route = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null);

/// <summary>Проблема с провенансом и местом — навигация «клик → к узлу/ребру» + инспектор.</summary>
public sealed record ProblemDto(
    string Id,
    string Severity,   // critical | warning | coverage | info
    string Kind,       // drift | layer-violation | unknown-role | shared-db | service-cycle | dangling-event | …
    string Title,
    string Location,   // nodeId | edgeId
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Via = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fix = null,
    // линза для фильтрации на фронте: architecture (реальные проблемы) | convention (дрейф от свода)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category = null,
    // отдельные факты агрегированной проблемы (типы дрейфа / пары нарушений) — для раскрытия списком
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ProblemItemDto>? Items = null);

/// <summary>Отдельный факт агрегированной проблемы: имя (тип / «From → To»), слой, короткая заметка.
/// File/Line — где живёт тип (для перехода «к файлу/плашке»); File пуст, если источник не дал путь.</summary>
public sealed record ProblemItemDto(
    string Name, string Layer, string Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null);

/// <summary>Среда развёртывания (E9.1 слой 2): <c>Kind</c>=compose (из override/named compose-файлов).
/// <c>Active</c> — какая среда сейчас применена (по ней собран граф/deploy). Для будущих источников
/// <c>Kind</c> может расширяться (topology/instance/profile).</summary>
public sealed record EnvironmentDto(
    string Id, string Name, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Active = null);

/// <summary>Свод конвенций, применённый при анализе: источник + сам пак (маркеры/fallback/правила) для просмотра.</summary>
public sealed record ConventionsDto(
    string Source,
    IReadOnlyList<ConvEntryDto> Markers,   // маркер (база → роль), уверенно
    IReadOnlyList<ConvEntryDto> Fallback,  // fallback (база → роль), эвристика
    IReadOnlyList<ConvRuleDto> Rules,      // ожидание: суффикс имени → маркер/роль (дрейф, если не выполнено)
    DtoMappingDto? DtoMapping = null);     // правило «маппинг не централизован» (суффиксы DTO, места-централизаторы, пороги)

public sealed record ConvEntryDto(string Name, string Role);
public sealed record ConvRuleDto(string Suffix, string Marker, string Role);
public sealed record DtoMappingDto(IReadOnlyList<string> Suffix, IReadOnlyList<string> CentralizeIn, int MinSites, int MinFiles);

/// <summary>HTTP-эндпоинт сервиса (L1). Source: controller ([Http*]/ControllerBase) | minimal (app.MapGet/...).
/// Route — полный путь, насколько разрешимо статически (склейка класс-[Route] + метод; константы резолвятся).</summary>
public sealed record EndpointDto(
    string Method,     // GET | POST | PUT | DELETE | PATCH
    string Route,      // путь (может быть пустым/относительным, если статически не разрешён)
    string Source,     // controller | minimal
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,   // тип-владелец (контроллер)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null);

/// <summary>DevOps-атрибуты узла-сервиса (E9, из compose): порты, наличие healthcheck, сети. Провенанс — compose.
/// Пустой (нет портов/сетей/hc) в узел не кладётся — поле остаётся null и не пишется.</summary>
public sealed record DeployDto(
    IReadOnlyList<PortDto> Ports,
    bool HasHealthcheck,
    IReadOnlyList<string> Networks);

/// <summary>Публикация порта. <c>Container</c> — порт внутри контейнера; <c>Host</c>/<c>HostIp</c> — если опубликован
/// наружу (иначе опущены). <c>Published</c>=true → доступен с хоста; при этом <c>HostIp</c>=null означает все
/// интерфейсы (0.0.0.0) — сигнал «наружу» (см. ExposedPortDetector).</summary>
public sealed record PortDto(
    string Container,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Host = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HostIp = null,
    bool Published = false);

/// <summary>Слой сервиса в L0-сводке: имя, здоровье, краткая заметка, число типов (превью L1).</summary>
public sealed record LayerSummaryDto(
    string Name,       // API | Application | Domain | Infrastructure | Contracts
    string Health,     // ok | warning | critical
    int Types,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Note = null);   // напр. «Domain → Infrastructure · нарушение слоя»

/// <summary>L0-сводка найденного сервиса (.sln): карточка со слоями. Id совпадает с namespace-префиксом его узлов.</summary>
public sealed record ServiceSummaryDto(
    string Id,         // namespace ({system})
    string Name,       // имя решения/сервиса
    string Health,     // ok | warning | critical (worst слоёв)
    IReadOnlyList<LayerSummaryDto> Layers);
