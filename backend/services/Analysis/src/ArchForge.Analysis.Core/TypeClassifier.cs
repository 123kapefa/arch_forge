using System.Text.RegularExpressions;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core;

// Классификатор ролей по convention-pack + линт дрейфа + покрытие + рёбра тип-графа.
// Перенос из C#-спайка; чистый (работает на RawType, без Roslyn).
public sealed class TypeClassifier(ConventionPack pack)
{
    public AnalysisResult Classify(IReadOnlyList<RawType> raw)
    {
        var types = new List<TypeInfo>();
        var drift = new List<string>();
        int marker = 0, heuristic = 0, unknown = 0;

        foreach (var r in raw)
        {
            string role = "unknown", via = "unknown";
            var td = new List<string>();

            if (r.Kind == "enum") { role = "Enum"; via = "heuristic"; }
            foreach (var b in r.Bases)
                if (via == "unknown" && pack.Markers.TryGetValue(b, out var mr)) { role = mr; via = "marker"; }
            if (via == "unknown")
                foreach (var b in r.Bases)
                    if (pack.Fallback.TryGetValue(b, out var fr)) { role = fr; via = "heuristic"; break; }
            if (via == "unknown" && r.Kind == "class")
            {
                var port = r.Bases.FirstOrDefault(b => b.Length > 1 && b[0] == 'I' && char.IsUpper(b[1]) && !pack.Markers.ContainsKey(b) && !pack.Fallback.ContainsKey(b));
                if (port is not null) { role = $"Adapter → {port}"; via = "heuristic"; }
            }
            if (via == "unknown" && r.Kind == "interface" && (r.Layer == "Application" || r.File.Contains("Abstractions")))
            {
                role = "Port"; via = "heuristic";
            }
            foreach (var (suffix, mk, rl) in pack.Expect)
                if (r.Name.EndsWith(suffix) && !r.Bases.Any(b => pack.Markers.TryGetValue(b, out var rr) && rr == rl))
                {
                    td.Add($"ожидается {mk}");
                    if (via == "unknown") { role = rl + "?"; via = "heuristic"; }
                }
            if (r.Name.EndsWith("Event") && !r.Bases.Contains("IDomainEvent") && r.Layer == "Domain")
            {
                td.Add("ожидается IDomainEvent");
                if (via == "unknown") { role = "DomainEvent?"; via = "heuristic"; }
            }
            if (via == "unknown" && r.Layer == "Contracts" && (r.Name.EndsWith("Request") || r.Name.EndsWith("Response"))) { role = "DTO"; via = "heuristic"; }
            if (via == "unknown" && r.Name.EndsWith("Input")) { role = "Input DTO"; via = "heuristic"; }
            if (via == "unknown" && r.Name.EndsWith("Options")) { role = "Config"; via = "heuristic"; }
            if (via == "unknown" && r.Name.EndsWith("Extensions")) { role = "DI/Extensions"; via = "heuristic"; }
            if (via == "unknown" && r.Name.EndsWith("Endpoints")) { role = "Endpoints"; via = "heuristic"; }
            if (via == "unknown" && r.Name == "Program") { role = "Entrypoint"; via = "heuristic"; }

            if (via == "marker") marker++;
            else if (via == "heuristic") heuristic++;
            else unknown++;

            if (td.Count > 0) drift.Add($"{r.Name} ({r.Layer}): {string.Join("; ", td)}");
            types.Add(new TypeInfo(r.Name, r.Kind, r.Layer, role, via, r.Bases, r.File, td));
        }

        return new AnalysisResult(types, BuildEdges(raw), new Coverage(marker, heuristic, unknown), drift);
    }

    // Рёбра между своими типами: implements (базы) + семантические Refs (uses/holds/raises/handles).
    // Если Refs пусты (синтаксический пол) — fallback на регекс по тексту первичного ctor.
    private static IReadOnlyList<TypeEdge> BuildEdges(IReadOnlyList<RawType> raw)
    {
        var names = raw.Select(r => r.Name).ToHashSet();
        var edges = new List<TypeEdge>();
        var seen = new HashSet<string>();

        foreach (var r in raw)
        {
            var implemented = new HashSet<string>();
            void Add(string to, string kind, bool skipIfImplemented = false)
            {
                if (to == r.Name || !names.Contains(to)) return;
                if (skipIfImplemented && implemented.Contains(to)) return; // не дублируем implements другим видом
                if (seen.Add($"{r.Name}|{to}|{kind}")) edges.Add(new TypeEdge(r.Name, to, kind));
            }

            foreach (var b in r.Bases)
                if (names.Contains(b)) { Add(b, "implements"); implemented.Add(b); }

            if (r.Refs.Count > 0)
                foreach (var rf in r.Refs) Add(rf.Name, rf.Kind, skipIfImplemented: true);
            else
            {
                var ek = r.Kind == "record" ? "holds" : "uses";
                foreach (Match m in Regex.Matches(r.CtorParams, "[A-Z][A-Za-z0-9_]*")) Add(m.Value, ek, skipIfImplemented: true);
            }
        }

        return edges;
    }
}
