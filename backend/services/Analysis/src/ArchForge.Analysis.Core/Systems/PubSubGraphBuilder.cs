using System.Globalization;

namespace ArchForge.Analysis.Core.Systems;

// #61: направление pub/sub из кода → ориентированные рёбра publishes/subscribes (сервис → шина) и честные
// dangling-event / orphaned-subscription (их считает уже готовый DanglingEventDetector).
//
// Честность (adr/0008): compose знает лишь «сервис подключён к шине» (uses-bus). Направление по шине выводим, только когда
// картина ПОЛНАЯ — иначе молчим, а не выдумываем «висячее событие»:
//   • каждый сервис, подключённый к шине, разобран и в его коде нашёлся хотя бы один сайт публикации/подписки;
//   • ни у одного из них нет «неизвестного направления» (клиент шины подключён, но использование не распознано);
//   • сервис подключён ровно к одной шине (иначе непонятно, на какую из них направлен его код);
//   • скан не усечён капом (иначе в Unknown-сайте и шина неполная).
// Если в системе есть сервис с кодом шины, но без uses-bus на шину скана (или с uses-bus на внешний брокер, или не привязан к узлу), находки понижаются до Coverage.
// Уверенность находок: если улики стороны, по которой сработал детектор, слабые (эвристика/строка без топика) —
// проблема понижается до Coverage. Совпадение по ИМЕНИ события здесь не проверяется — только по шине целиком;
// событийная детализация — отдельный срез.
public static class PubSubGraphBuilder
{
    private const double MinWarningConfidence = 0.6;   // слабее — не утверждаем, а честно показываем как «покрытие»
    private const int MaxItems = 20;                   // потолок фактов в одной проблеме

    public static SystemGraph Enrich(SystemGraph graph, IReadOnlyList<ServiceAnalysis> analyses, Profiles.ServiceNaming? naming = null)
    {
        if (analyses.All(a => a.BusSites.Count == 0)) return graph;
        naming ??= Profiles.ServiceNaming.Default;

        var bySystem = graph.Nodes
            .Where(n => n.Kind == SystemNodeKind.Service)
            .GroupBy(n => HttpTargetResolver.SystemOf(n.Id), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var topicIds = graph.Nodes.Where(n => n.Kind == SystemNodeKind.Topic).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        // узел сервиса → сайты его кода (не разобрался / не привязан к узлу с сайтами — «outsider», находки понижаются)
        var sitesByNode = new Dictionary<string, List<BusSite>>(StringComparer.Ordinal);
        var unattributed = false;   // сервис с кодом шины не привязан к узлу — куда он говорит, неизвестно
        foreach (var sa in analyses)
        {
            var node = sa.Result is null ? null : HttpCallGraphBuilder.SourceNodeId(sa, bySystem, naming);
            if (node is null) { if (sa.BusSites.Count > 0) unattributed = true; continue; }
            if (!sitesByNode.TryGetValue(node, out var list)) sitesByNode[node] = list = [];
            list.AddRange(sa.BusSites);
        }

        // шины сервиса и сервисы шины — по compose-рёбрам uses-bus
        var busesOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in graph.Edges)
            if (e.Kind == "uses-bus" && !e.Deferred && topicIds.Contains(e.Target))
            {
                if (!busesOf.TryGetValue(e.Source, out var l)) busesOf[e.Source] = l = [];
                if (!l.Contains(e.Target)) l.Add(e.Target);
            }
        // Код шины вне compose-подключений: сервис с сайтами, но без uses-bus на шину скана (appsettings/vault/k8s), или с uses-bus на внешний
        // брокер. Такой сервис может говорить с любой шиной системы — находки по ним понижаем до «покрытия», а не утверждаем.
        var externalBus = graph.Edges.Where(e => e.Kind == "uses-bus" && !topicIds.Contains(e.Target)).Select(e => e.Source).ToHashSet(StringComparer.Ordinal);
        var outsiders = unattributed || sitesByNode.Any(kv => kv.Value.Count > 0 && (!busesOf.ContainsKey(kv.Key) || externalBus.Contains(kv.Key)));
        var servicesOf = busesOf.SelectMany(kv => kv.Value.Select(b => (Bus: b, Svc: kv.Key)))
            .GroupBy(x => x.Bus, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Svc).ToList(), StringComparer.Ordinal);

        var added = new List<SystemEdge>();
        var sitesOfBus = new Dictionary<string, List<BusSite>>(StringComparer.Ordinal);
        foreach (var (bus, services) in servicesOf)
        {
            var complete = services.All(s =>
                busesOf[s].Count == 1 && sitesByNode.TryGetValue(s, out var sites) && sites.Count > 0
                && sites.All(x => x.Direction != BusDirection.Unknown));
            if (!complete) continue;                                               // неполная картина → молчим

            var all = new List<BusSite>();
            foreach (var s in services.Order(StringComparer.Ordinal))
                foreach (var dir in new[] { BusDirection.Publish, BusDirection.Subscribe })
                {
                    var of = sitesByNode[s].Where(x => x.Direction == dir).ToList();
                    if (of.Count == 0) continue;
                    all.AddRange(of);
                    added.Add(new SystemEdge(s, bus, dir == BusDirection.Publish ? "publishes" : "subscribes", EdgeVia.Code,
                        Math.Round(of.Max(x => x.Confidence), 2), Origin: OriginOf(of, dir)));
                }
            sitesOfBus[bus] = all;
        }
        if (added.Count == 0) return graph;

        var enriched = graph with { Edges = [.. graph.Edges, .. added] };
        var found = DanglingEventDetector.Detect(enriched)
            .Where(p => p.Kind is "dangling-event" or "orphaned-subscription")
            .Select(p => Refine(p, sitesOfBus, outsiders))
            .ToList();
        return enriched with { Problems = found.Count > 0 ? [.. enriched.Problems, .. found] : enriched.Problems };
    }

    // E5.1: самый надёжный из сайтов — место ребра в коде (при равенстве — по пути/строке, детерминированно)
    private static EdgeOrigin OriginOf(List<BusSite> sites, BusDirection dir)
    {
        var best = sites.OrderByDescending(x => x.Confidence).ThenBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Line).First();
        var verb = dir == BusDirection.Publish ? "публикация" : "подписка";
        var what = best.Name.Length > 0 ? $"{verb} «{best.Name}»" : $"{verb} (имя не определено)";
        var more = sites.Count > 1 ? $" · ещё мест: {sites.Count - 1}" : "";
        var basis = best.Basis switch { BusBasis.Marker => "marker", BusBasis.String => "string", _ => "heuristic" };
        return new EdgeOrigin(best.File.Length > 0 ? best.File : null, best.Line > 0 ? best.Line : null, what + more, basis);
    }

    // Факты проблемы — места кода по стороне, на которой она основана (публикации для dangling, подписки для orphaned);
    // слабые улики → Coverage вместо Warning.
    private static SystemProblem Refine(SystemProblem p, Dictionary<string, List<BusSite>> sitesOfBus, bool outsiders)
    {
        var dir = p.Kind == "dangling-event" ? BusDirection.Publish : BusDirection.Subscribe;
        var sites = sitesOfBus.TryGetValue(p.Location, out var all) ? all.Where(x => x.Direction == dir).ToList() : [];
        var items = sites
            .OrderByDescending(x => x.Confidence).ThenBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Line)
            .Take(MaxItems)
            .Select(x => new ProblemItem(x.Name.Length > 0 ? x.Name : "(имя не определено)", "", Note(x), x.File, x.Line))
            .ToList();
        var weak = sites.Count == 0 || sites.Max(x => x.Confidence) < MinWarningConfidence;
        var title = weak ? $"{p.Title} — низкая уверенность (эвристика)" : p.Title;
        if (outsiders) title += " — есть сервисы с кодом шины без подключения к ней в compose";
        return p with
        {
            Severity = weak || outsiders ? ProblemSeverity.Coverage : p.Severity,
            Title = title,
            Items = items,
        };
    }

    private static string Note(BusSite s) =>
        $"уверенность {s.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}: " + s.Basis switch
        {
            BusBasis.Marker => "маркер в коде",
            BusBasis.String => "строковый топик",
            BusBasis.Convention => "эвристика по имени",
            _ => "тип не определён",
        };
}
