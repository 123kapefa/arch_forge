using System.Text.Json.Serialization;

namespace ArchForge.Contracts;

// E3.4: реконструкция потока («что происходит при запросе X») по call-graph. Отдельный ответ
// GET /api/system/flow — контракт графа не менялся (api-contracts). Статика: вероятный happy-path, без рантайма.

/// <summary>Цепочка вызовов от точки входа. <c>Steps</c> — дерево в порядке обхода (родитель раньше детей;
/// <c>Parent</c> — индекс шага-родителя, null — корень). <c>Truncated</c>=true — цепочка продолжается за лимитом.</summary>
public sealed record FlowDto(
    string Entry,                       // id узла-сервиса, откуда начат поток
    string EntryName,
    IReadOnlyList<FlowStepDto> Steps,
    bool Truncated,
    FlowLimitsDto Limits,
    IReadOnlyList<string> Notes,        // честные оговорки: уровень сервиса, шина без имён событий, деградации карты…
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Endpoint = null);   // «GET /orders» — входной endpoint, если задан и найден

public sealed record FlowLimitsDto(int MaxDepth, int MaxSteps);

/// <summary>Один хоп. <c>Kind</c>: http | publish | consume | data | bus | external. <c>Sync</c>: true — ждём ответа
/// (http), false — асинхронно (шина), null — не применимо (данные). <c>Status</c>: ok | unmatched (маршрут не найден у цели) |
/// unresolved (вызов есть, детали не определены) | cycle (сервис уже в цепочке) | repeat (раскрыт выше) | truncated (лимит глубины).</summary>
public sealed record FlowStepDto(
    int Index,
    int Depth,
    string From,
    string To,
    string ToName,
    string Kind,
    string Label,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Parent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Sync = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EdgeId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Confidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Via = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HttpCallDto>? Calls = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);
