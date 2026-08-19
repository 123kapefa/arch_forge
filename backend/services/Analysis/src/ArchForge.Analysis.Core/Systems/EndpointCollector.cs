using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Собирает HTTP-эндпоинты сервиса для L1 (#71). Контроллеры — из членов типов (kind="endpoint": verb + склеенный
// route уже извлечены RoslynSemanticTypeSource'ом). Minimal API (app.MapGet/...) добавляется отдельным источником.
public static class EndpointCollector
{
    /// <summary>Эндпоинты из членов-типов (контроллеры). Детерминированный порядок: route, метод, тип.</summary>
    public static IReadOnlyList<EndpointDto> FromTypes(IEnumerable<TypeInfo> types) =>
        types.SelectMany(t => t.Members
                .Where(m => m.Kind == "endpoint" && m.HttpMethod is not null)
                .Select(m => new EndpointDto(m.HttpMethod!, m.Route ?? "", "controller", t.Name, m.Line)))
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ToList();
}
