using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Системный IR (SystemGraph) → аддитивный контракт GraphDto (Фаза 1.5).
// Узлы service/datastore/topic/external; рёбра с Via/Confidence; проблемы → ProblemDto; здоровье — из проблем.
public static class SystemGraphMapper
{
    public static GraphDto ToDto(SystemGraph g, string project,
        IReadOnlyDictionary<string, MicroserviceLayerBuilder.Entry>? nodeLayers = null,
        ConventionPack? pack = null)
    {
        // здоровье узла — по самой суровой проблеме, где он location (Critical=0 < Warning=1 < Coverage=2)
        var worstByLocation = g.Problems
            .GroupBy(p => p.Location, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(p => (int)p.Severity), StringComparer.Ordinal);

        var nodes = g.Nodes.Select(n =>
        {
            var problemHealth = worstByLocation.TryGetValue(n.Id, out var sev) ? HealthOf(sev) : "ok";
            // слои микросервиса (L0: «внутри сервиса — слои») + их здоровье учитываем в health узла
            var layers = nodeLayers is not null && nodeLayers.TryGetValue(
                MicroserviceLayerBuilder.NodeKey(n.Id, n.Name), out var entry) ? entry : null;
            var health = layers is null ? problemHealth : Worse(problemHealth, layers.Health);
            return new NodeDto(
                n.Id, n.Name, KindString(n.Kind), Layer: "", Role: "", Via: "", File: "", Drift: [],
                Level: LevelOf(n.Kind), Health: health, Layers: layers?.Layers, Root: layers?.Root);
        }).ToList();

        var edges = g.Edges.Select(e => new EdgeDto(
            $"{e.Source}-{e.Kind}->{e.Target}", e.Source, e.Target, e.Kind,
            Via: ViaString(e.Via), Confidence: e.Confidence)).ToList();

        var problems = g.Problems
            .Select((p, i) => new ProblemDto($"p{i}", SeverityString(p.Severity), p.Kind, p.Title, p.Location,
                Category: p.Category,
                Items: p.Items is null ? null : p.Items.Select(it => new ProblemItemDto(it.Name, it.Layer, it.Note)).ToList()))
            .ToList();

        var health = g.Problems.Any(p => p.Severity == ProblemSeverity.Critical) ? "critical"
            : g.Problems.Any(p => p.Severity == ProblemSeverity.Warning) ? "warning"
            : "ok";

        // TODO(P1.5-G): системное покрытие — заглушка (total=узлы). Настоящая агрегация per-service —
        // когда per-service тип-графы вольются в системный IR (иначе фронт-виджет покажет 0% marker).
        var conventions = pack is null ? null : new ConventionsDto(
            pack.Source,
            pack.Markers.Select(m => new ConvEntryDto(m.Key, m.Value)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
            pack.Fallback.Select(m => new ConvEntryDto(m.Key, m.Value)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
            pack.Expect.Select(r => new ConvRuleDto(r.Suffix, r.Marker, r.Role)).ToList());
        return new GraphDto(project, nodes, edges,
            new CoverageDto(0, 0, 0, nodes.Count), Drift: [],
            Health: health,
            Problems: problems.Count > 0 ? problems : null,
            Conventions: conventions);
    }

    private static string KindString(SystemNodeKind k) => k switch
    {
        SystemNodeKind.Service => "service",
        SystemNodeKind.Datastore => "datastore",
        SystemNodeKind.Topic => "topic",
        SystemNodeKind.External => "external",
        _ => "unknown",
    };

    private static string LevelOf(SystemNodeKind k) => k == SystemNodeKind.Service ? "service" : "system";

    private static string ViaString(EdgeVia v) => v switch
    {
        EdgeVia.Compose => "compose",
        EdgeVia.Config => "config",
        EdgeVia.Code => "code",
        _ => "unknown",
    };

    private static string SeverityString(ProblemSeverity s) => s switch
    {
        ProblemSeverity.Critical => "critical",
        ProblemSeverity.Warning => "warning",
        ProblemSeverity.Coverage => "coverage",
        _ => "info",
    };

    // health красит только critical/warning; coverage и info (дрейф) — не красят узел
    private static string HealthOf(int worstSeverity) => worstSeverity switch { 0 => "critical", 1 => "warning", _ => "ok" };

    private static int HealthRank(string h) => h switch { "critical" => 0, "warning" => 1, _ => 2 };
    private static string Worse(string a, string b) => HealthRank(a) <= HealthRank(b) ? a : b;
}
