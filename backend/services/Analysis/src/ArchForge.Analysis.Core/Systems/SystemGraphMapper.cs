using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Системный IR (SystemGraph) → аддитивный контракт GraphDto (Фаза 1.5).
// Узлы service/datastore/topic/external; рёбра с Via/Confidence; проблемы → ProblemDto; здоровье — из проблем.
public static class SystemGraphMapper
{
    public static GraphDto ToDto(SystemGraph g, string project)
    {
        // здоровье узла — по самой суровой проблеме, где он location (Critical=0 < Warning=1 < Coverage=2)
        var worstByLocation = g.Problems
            .GroupBy(p => p.Location, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(p => (int)p.Severity), StringComparer.Ordinal);

        var nodes = g.Nodes.Select(n => new NodeDto(
            n.Id, n.Name, KindString(n.Kind), Layer: "", Role: "", Via: "", File: "", Drift: [],
            Level: LevelOf(n.Kind),
            Health: worstByLocation.TryGetValue(n.Id, out var sev) ? HealthOf(sev) : "ok")).ToList();

        var edges = g.Edges.Select(e => new EdgeDto(
            $"{e.Source}-{e.Kind}->{e.Target}", e.Source, e.Target, e.Kind,
            Via: ViaString(e.Via), Confidence: e.Confidence)).ToList();

        var problems = g.Problems
            .Select((p, i) => new ProblemDto($"p{i}", SeverityString(p.Severity), p.Kind, p.Title, p.Location))
            .ToList();

        var health = g.Problems.Any(p => p.Severity == ProblemSeverity.Critical) ? "critical"
            : g.Problems.Any(p => p.Severity == ProblemSeverity.Warning) ? "warning"
            : "ok";

        return new GraphDto(project, nodes, edges,
            new CoverageDto(0, 0, 0, nodes.Count), Drift: [],
            Health: health,
            Problems: problems.Count > 0 ? problems : null);
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
        _ => "coverage",
    };

    // coverage-проблема не красит узел (визуально), только critical/warning
    private static string HealthOf(int worstSeverity) => worstSeverity switch { 0 => "critical", 1 => "warning", _ => "ok" };
}
