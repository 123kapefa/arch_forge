using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Собирает HTTP-эндпоинты сервиса для L1 (#71) из ДВУХ источников: контроллеры — из членов типов
// (kind="endpoint": verb + склеенный route уже извлечены RoslynSemanticTypeSource'ом); minimal API —
// из синтаксического скана app.MapGet/… (EndpointHit). Порядок детерминированный: route, метод, тип.
public static class EndpointCollector
{
    /// <summary>Эндпоинты сервиса: контроллеры (из типов) + minimal API (из hit'ов), отсортированные.</summary>
    public static IReadOnlyList<EndpointDto> From(IEnumerable<TypeInfo> types, IEnumerable<EndpointHit> minimal)
    {
        var fromControllers = types.SelectMany(t => t.Members
            .Where(m => m.Kind == "endpoint" && m.HttpMethod is not null)
            .Select(m => new EndpointDto(m.HttpMethod!, m.Route ?? "", "controller", t.Name, m.Line)));

        var fromMinimal = minimal
            .Select(h => new EndpointDto(h.Method, NormalizeRoute(h.Route), "minimal", Type: null, h.Line));

        return fromControllers.Concat(fromMinimal)
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Type ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Только контроллеры (shared kernel и т.п. — minimal API там нет).</summary>
    public static IReadOnlyList<EndpointDto> FromTypes(IEnumerable<TypeInfo> types) => From(types, []);

    private static string NormalizeRoute(string route) =>
        string.IsNullOrEmpty(route) ? "" : route.StartsWith('/') ? route : "/" + route;
}
