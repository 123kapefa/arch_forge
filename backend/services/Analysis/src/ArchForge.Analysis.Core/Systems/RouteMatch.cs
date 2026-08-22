using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Сопоставление маршрута вызова с эндпоинтом цели (E3.1). Статическое, КОНСЕРВАТИВНОЕ: сравниваем по СЕГМЕНТАМ
// пути; сегмент-плейсхолдер эндпоинта ({id}, {id:int}) — wildcard. Verb должен совпасть, если у эндпоинта он задан.
// Пустой/относительный маршрут эндпоинта не матчим (нечего сравнивать) — результат false остаётся ЧЕСТНЫМ (adr/0008):
// «цель есть, эндпоинт не подтверждён» лучше, чем ложное совпадение.
public static class RouteMatch
{
    public static bool Matches(string method, string callPath, IReadOnlyList<EndpointDto> endpoints)
    {
        var call = Segments(callPath);
        foreach (var ep in endpoints)
        {
            if (!string.IsNullOrEmpty(ep.Method) && !ep.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(ep.Route)) continue;
            if (SegmentsMatch(call, Segments(ep.Route))) return true;
        }
        return false;
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool SegmentsMatch(string[] call, string[] tmpl)
    {
        if (call.Length != tmpl.Length) return false;
        for (var i = 0; i < call.Length; i++)
        {
            var t = tmpl[i];
            if (t.StartsWith('{') && t.EndsWith('}')) continue;                        // плейсхолдер — любой сегмент
            if (!t.Equals(call[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
