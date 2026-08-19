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

    /// <summary>ключ: "{namespace}::{нормализованное имя микросервиса}" → сводка слоёв.</summary>
    public static IReadOnlyDictionary<string, Entry> Build(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (svc, location, analysis) in analyses)
        {
            if (analysis is null) continue;
            var groups = analysis.Types.GroupBy(t => MicroserviceOf(t.File) ?? Last(location), StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var names = g.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                var edges = analysis.Edges.Where(e => names.Contains(e.From) && names.Contains(e.To)).ToList();
                var types = g.ToList();
                var layers = ServiceSummaryBuilder.Layers(types, edges);
                if (layers.Count == 0) continue;
                // корень папки микросервиса (для drill L1: /api/map по нему) — .../Services/<Name> или корень .sln
                var root = g.Select(t => ServiceRootOf(t.File)).FirstOrDefault(r => r is not null) ?? svc.Root;
                var endpoints = EndpointCollector.FromTypes(types); // HTTP-эндпоинты сервиса (L1-список; #71)
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

    /// <summary>Ключ compose-узла для сопоставления со сводкой: namespace-префикс id + норм. имя.</summary>
    public static string NodeKey(string nodeId, string nodeName) => $"{Prefix(nodeId)}::{Normalize(nodeName)}";

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
