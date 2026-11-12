using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Сопоставление маршрута вызова с эндпоинтом цели (E3.1). Статическое, КОНСЕРВАТИВНОЕ: сравниваем по СЕГМЕНТАМ
// пути; сегмент-плейсхолдер эндпоинта ({id}, {id:int}) — wildcard. Verb должен совпасть, если у эндпоинта он задан.
// Пустой/относительный маршрут эндпоинта не матчим (нечего сравнивать) — результат false остаётся ЧЕСТНЫМ (adr/0008):
// «цель есть, эндпоинт не подтверждён» лучше, чем ложное совпадение.
public static class RouteMatch
{
    /// <summary>Трёхзначный итог для UI: true — эндпоинт найден; false — нет; null — подстановка в маршруте вызова
    /// ({version}) могла бы совпасть с литеральным сегментом, но проверить нельзя («не проверялось», а не ложное «не найден»).</summary>
    public static bool? Match(string method, string callPath, IReadOnlyList<EndpointDto> endpoints)
    {
        if (Matches(method, callPath, endpoints)) return true;
        return callPath.Contains('{') && MatchesLoose(method, callPath, endpoints) ? null : false;
    }

    // Мягкий вариант: подстановка вызова совпадает с ЛЮБЫМ сегментом эндпоинта (нужен лишь чтобы отличить «неизвестно» от «нет»).
    private static bool MatchesLoose(string method, string callPath, IReadOnlyList<EndpointDto> endpoints)
    {
        var call = Segments(callPath);
        foreach (var ep in endpoints)
        {
            if (!string.IsNullOrEmpty(ep.Method) && !ep.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(ep.Route)) continue;
            var tmpl = Segments(ep.Route);
            if (tmpl.Length != call.Length) continue;
            var ok = true;
            for (var i = 0; i < call.Length && ok; i++)
                ok = (call[i].StartsWith('{') && call[i].EndsWith('}')) || (tmpl[i].StartsWith('{') && tmpl[i].EndsWith('}'))
                     || tmpl[i].Equals(call[i], StringComparison.OrdinalIgnoreCase);
            if (ok) return true;
        }
        return false;
    }

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
            // подстановка в вызове ($"/items/{id}") значения не знает: сходится только с плейсхолдером эндпоинта,
            // а с литеральным сегментом («reserve») — нет (консервативно, не плодим ложные совпадения)
            if (call[i].StartsWith('{') && call[i].EndsWith('}')) return false;
            if (!t.Equals(call[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
