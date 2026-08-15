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
    IReadOnlyList<EnvironmentDto>? Environments = null);   // зарезервировано под P1.5; в Фазе 1.5 не заполняется

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
    IReadOnlyList<MemberDto>? Members = null);             // члены type-узла (эндпоинты контроллера и т.п.)

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category = null);

/// <summary>Контур окружения. Зарезервировано под P1.5 (топология/инстанс/профиль); в Фазе 1.5 не заполняется.</summary>
public sealed record EnvironmentDto(string Id, string Name, string Kind); // topology | instance | profile
