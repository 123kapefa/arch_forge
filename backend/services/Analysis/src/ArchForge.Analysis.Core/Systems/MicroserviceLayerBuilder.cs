using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Разбивает per-.sln анализ по МИКРОСЕРВИСАМ (папка под `src/Services/<Имя>/`) и строит сводку слоёв каждого,
// чтобы на L0 у каждого сервиса-узла были ЕГО слои (а не общие по репозиторию).
// Нет `Services/`-структуры (одно-сервисный .sln) → весь .sln = один сервис (ключ по имени namespace).
// Совпадение с compose-узлом — по namespace + нормализованному имени (catalog-api ↔ Catalog).
public static class MicroserviceLayerBuilder
{
    public sealed record Entry(string Health, string? Root, IReadOnlyList<LayerSummaryDto> Layers,
        IReadOnlyList<EndpointDto> Endpoints);

    /// <summary>ключ: "{namespace}::{нормализованное имя микросервиса}" → сводка слоёв.
    /// <paramref name="composeServices"/> (id+имя compose-узлов) позволяет привязать код ВНЕ /Services/
    /// (gateway и т.п.) к его узлу по имени — иначе он остаётся без слоёв/endpoints (#75).</summary>
    public static IReadOnlyDictionary<string, Entry> Build(IReadOnlyList<ServiceAnalysis> analyses,
        IReadOnlyList<(string Id, string Name)>? composeServices = null)
    {
        // имя compose-сервиса (норм. alnum) → имя: для сопоставления сегмента пути кода вне /Services/ с узлом (#75)
        var byNorm = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, name) in composeServices ?? [])
            byNorm.TryAdd(NormAlnum(name), name); // при коллизии норм.-имён — первое (детерминизм по порядку узлов)

        var byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var sa in analyses)
        {
            if (sa.Result is null) continue;
            var location = sa.Location;
            // Одно-сервисный .sln (нет партиции /Services/): весь код = один сервис. Несматченные проекты
            // (Auth.Domain/Application/…) отправляем на ГЛАВНЫЙ узел location, иначе они падают в Last(location)
            // и теряются, а узел показывает лишь проект, совпавший по имени (auth-api ← Auth.API) — #81.
            // Признак партиции — тот же MicroserviceOf, что и группировка (иначе ключи схлопнутся и данные потеряются).
            // В мульти-сервисном репо главного нет — catch-all остаётся общим (не привязываем чужой код).
            var hasServices = sa.Result.Types.Any(t => MicroserviceOf(t.File) is not null);
            var primary = hasServices ? null : PrimaryNode(sa.Result.Types, byNorm, sa.Service.Name);
            // minimal-API эндпоинты сервиса — по тому же ключу микросервиса, что и типы (#71)
            var miniByMs = sa.MinimalEndpoints
                .GroupBy(h => MsKey(h.File, location, byNorm, primary), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => (IReadOnlyList<EndpointHit>)x.ToList(), StringComparer.OrdinalIgnoreCase);
            var groups = sa.Result.Types.GroupBy(t => MsKey(t.File, location, byNorm, primary), StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var names = g.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                var edges = sa.Result.Edges.Where(e => names.Contains(e.From) && names.Contains(e.To)).ToList();
                var types = g.ToList();
                var layers = ServiceSummaryBuilder.Layers(types, edges);
                if (layers.Count == 0) continue;
                // корень папки для drill L1 (/api/map по нему): /Services/<Name> — если это /Services/; иначе
                // ОБЩИЙ каталог-префикс файлов группы (gateway → его папка; одно-сервисный .sln с типами в разных
                // проектах → их общий корень, не сужаем до одного проекта, #78); последний фолбэк — корень .sln.
                var svcRoot = g.Select(t => ServiceRootOf(t.File)).FirstOrDefault(r => r is not null);
                var root = svcRoot ?? CommonDirPrefix(types.Select(t => t.File)) ?? sa.Service.Root;
                var mini = miniByMs.TryGetValue(g.Key, out var m) ? m : [];
                var endpoints = EndpointCollector.From(types, mini); // контроллеры + minimal API (L1-список; #71)
                byKey[$"{location}::{Normalize(g.Key)}"] = new Entry(ServiceSummaryBuilder.WorstHealth(layers), root, layers, endpoints);
            }
        }
        return byKey;
    }

    // Корень папки микросервиса из пути файла: «.../src/Services/Catalog/...» → «.../src/Services/Catalog».
    private static string? ServiceRootOf(string file)
    {
        var p = file.Replace('\\', '/');
        var i = p.IndexOf("/Services/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var after = i + "/Services/".Length;
        var slash = p.IndexOf('/', after);
        return slash > after ? p[..slash] : null;
    }

    // Общий каталог-префикс путей (посегментно): для кода вне /Services/ даёт папку, охватывающую ВСЕ файлы группы —
    // gateway (все файлы в одном проекте) → его папка; сервис с типами в нескольких проектах → их общий корень
    // (не сужаем до одного проекта). Иначе drill грузил бы корень всего backend либо только часть сервиса (#78).
    private static string? CommonDirPrefix(IEnumerable<string> files)
    {
        string[]? common = null;
        foreach (var f in files)
        {
            var p = f.Replace('\\', '/');
            var slash = p.LastIndexOf('/');
            if (slash <= 0) continue;                       // нет каталога (или корневой) — пропускаем
            var dir = p[..slash].Split('/');                // сегменты каталога файла (с ведущим "" для абсолютных)
            if (common is null) { common = dir; continue; }
            var n = Math.Min(common.Length, dir.Length);
            var k = 0;
            while (k < n && string.Equals(common[k], dir[k], StringComparison.Ordinal)) k++;
            common = common[..k];
        }
        return common is { Length: > 0 } ? string.Join('/', common) : null;
    }

    /// <summary>Ключ compose-узла для сопоставления со сводкой: namespace-префикс id + норм. имя.</summary>
    public static string NodeKey(string nodeId, string nodeName) => $"{Prefix(nodeId)}::{Normalize(nodeName)}";

    // Ключ микросервиса для файла: /Services/<Name> → Name; иначе — сопоставление сегмента под /src/ с именем
    // compose-узла (код вне /Services/, напр. gateway, #75); иначе — главный узел (одно-сервисный .sln, #81)
    // или общий хвост namespace (catch-all мульти-сервиса).
    private static string MsKey(string file, string location, IReadOnlyDictionary<string, string> byNorm, string? primaryCatchAll)
    {
        var ms = MicroserviceOf(file);
        if (ms is not null) return ms;
        if (byNorm.Count > 0)
            foreach (var cand in CandidateSegments(file))
                if (byNorm.TryGetValue(NormAlnum(cand), out var name)) return name;
        return primaryCatchAll ?? Last(location);
    }

    // Сателлит-деплой (не несёт код сервиса): миграторы/сидеры/крон-джобы — исключаем из кандидатов на «главного».
    // Только специфичные job-маркеры: «worker» намеренно НЕ включён — это легитимный тип сервиса (.NET Worker).
    private static readonly string[] SatelliteMarkers = { "migrat", "seed", "cron" };
    private static bool IsSatellite(string name) => SatelliteMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    // Главный узел одно-сервисного .sln (на него уходит весь код, #81). Из compose-узлов берём НЕ-сателлиты,
    // среди них — с бóльшим числом совпавших проектов; при равенстве — узел, чьё имя ≈ имени .sln (auth-service ≡
    // AuthService). ВАЖНО: возвращаем узел только при ПОЛОЖИТЕЛЬНОМ сигнале (совпал проект ИЛИ имя ≈ .sln) — иначе
    // не гадаем по алфавиту среди всех узлов системы (мог бы уехать на чужой sidecar вроде adminer); null → обычный
    // catch-all (безопасная деградация к прежнему поведению).
    private static string? PrimaryNode(IReadOnlyList<TypeInfo> types, IReadOnlyDictionary<string, string> byNorm, string slnName)
    {
        var candidates = byNorm.Values.Where(n => !IsSatellite(n)).Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) return null;
        var counts = candidates.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        foreach (var t in types)
            foreach (var cand in CandidateSegments(t.File))
                if (byNorm.TryGetValue(NormAlnum(cand), out var name) && counts.ContainsKey(name)) { counts[name]++; break; }
        var slnNorm = NormAlnum(slnName);
        var best = candidates
            .OrderByDescending(n => counts[n])                                   // больше совпавших проектов
            .ThenByDescending(n => NameMatchesSln(NormAlnum(n), slnNorm) ? 1 : 0) // имя узла ≈ имя .sln
            .ThenBy(n => n, StringComparer.Ordinal)                              // детерминизм
            .First();
        return counts[best] > 0 || NameMatchesSln(NormAlnum(best), slnNorm) ? best : null; // без сигнала — не гадаем
    }

    // Имя узла ≈ имя .sln по NormAlnum (равно или одно содержит другое). Известное ограничение: подстрока без
    // границ сегмента может дать ложное совпадение при слишком общих именах — тай-брейк, не основной сигнал.
    private static bool NameMatchesSln(string nodeNorm, string slnNorm) =>
        slnNorm.Length > 0 && nodeNorm.Length > 0 && (nodeNorm == slnNorm || nodeNorm.Contains(slnNorm) || slnNorm.Contains(nodeNorm));

    // Кандидаты имени сервиса из пути: только ВЕРХНИЕ папки проекта после /src/ (+ их последняя dotted-часть,
    // «Kalmykov.ApiGateway» → «ApiGateway»). Ограничение глубины — чтобы подпапки (напр. Routes/Catalog в gateway)
    // не приписали код чужому сервису (#75). Для матчинга с именем compose-узла.
    private const int MaxCandidateDepth = 2; // проект обычно /src/<Area>/<Project> — двух сегментов хватает
    private static IEnumerable<string> CandidateSegments(string file)
    {
        var p = file.Replace('\\', '/');
        var i = p.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        var rest = i >= 0 ? p[(i + 5)..] : p;
        var parts = rest.Split('/');
        var limit = Math.Min(parts.Length - 1, MaxCandidateDepth); // без последнего сегмента-файла; только верх
        for (var k = 0; k < limit; k++)
        {
            var seg = parts[k];
            if (seg.Length == 0) continue;
            yield return seg;
            var dot = seg.LastIndexOf('.');
            if (dot > 0 && dot < seg.Length - 1) yield return seg[(dot + 1)..];
        }
    }

    // Имя → только буквы/цифры в нижнем регистре: «api-gateway» ≡ «ApiGateway» → «apigateway».
    private static string NormAlnum(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // Микросервис из пути: сегмент после "/Services/"; null — если структуры Services/ нет.
    private static string? MicroserviceOf(string file)
    {
        var p = file.Replace('\\', '/');
        var i = p.IndexOf("/Services/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = p[(i + "/Services/".Length)..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : null;
    }

    // Имя сервиса → нормализованное: нижний регистр без деплой-суффиксов (catalog-api → catalog).
    private static readonly string[] Suffixes = { "-api", "-service", "-ui", "-app", "-web", "-server" };
    private static string Normalize(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var s in Suffixes)
            if (n.EndsWith(s, StringComparison.Ordinal)) return n[..^s.Length];
        return n;
    }

    private static string Prefix(string id) { var i = id.LastIndexOf('/'); return i > 0 ? id[..i] : ""; }
    private static string Last(string ns) { var i = ns.LastIndexOf('/'); return i >= 0 ? ns[(i + 1)..] : ns; }
}
