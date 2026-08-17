using System.Text.RegularExpressions;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Core;

// Классификатор ролей по convention-pack + линт дрейфа + покрытие + рёбра тип-графа.
// Перенос из C#-спайка; чистый (работает на RawType, без Roslyn). Пак передаётся в Classify — он свой у
// каждого проекта (грузится из файла), поэтому классификатор без состояния.
public sealed class TypeClassifier
{
    public AnalysisResult Classify(IReadOnlyList<RawType> raw, ConventionPack pack)
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
            // Свод — про СЕРВИСНЫЙ код (Domain/Application/Api/…). Shared kernel сам ОПРЕДЕЛЯЕТ конвенции
            // (Core: ICommandHandler/IQuery; Authentication: TokenForwardingHandler — HTTP DelegatingHandler;
            // Framework: ExceptionMiddleware), а tests — тест-дубли (FakeHttpMessageHandler, CapturingHandler).
            // К ним сервисные ожидания неприменимы: иначе ложный дрейф «ожидается ICommandHandler» на них.
            if (r.Layer is not ("Shared" or "tests"))
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
            if (via == "unknown" && (r.Name.EndsWith("Result") || r.Name.EndsWith("Dto"))) { role = "Result"; via = "heuristic"; }
            if (via == "unknown" && r.Layer == "tests") { role = "Test"; via = "heuristic"; }
            // Инфраструктурные примитивы по суффиксу — читаемые роли вместо «unknown» (особенно для shared
            // kernel: Middleware/Provider/Checker/Dispatcher/…). Описательно, на дрейф не влияет.
            if (via == "unknown" && InfraRole(r.Name) is { } ir) { role = ir; via = "heuristic"; }
            if (via == "unknown" && r.Kind == "interface") { role = "Port"; via = "heuristic"; } // интерфейс без маркера — порт/контракт

            if (via == "marker") marker++;
            else if (via == "heuristic") heuristic++;
            else unknown++;

            if (td.Count > 0) drift.Add($"{r.Name} ({r.Layer}): {string.Join("; ", td)}");
            var members = r.Members
                .Select(m => new MemberInfo(m.Name, m.Kind, m.Signature, m.HttpMethod, m.Route, m.Line))
                .ToList();
            types.Add(new TypeInfo(r.Name, r.Kind, r.Layer, role, via, r.Bases, r.File, td, members));
        }

        return new AnalysisResult(types, BuildEdges(raw), new Coverage(marker, heuristic, unknown), drift);
    }

    // Описательная роль инфраструктурного типа по суффиксу (читаемость shared kernel и прочей инфры).
    // Только когда маркер/fallback молчат — не спорит с достоверной классификацией по базам.
    private static string? InfraRole(string name) => name switch
    {
        _ when name.EndsWith("Middleware") => "Middleware",
        _ when name.EndsWith("Interceptor") => "Interceptor",
        _ when name.EndsWith("Behavior") => "Pipeline Behavior",
        _ when name.EndsWith("Dispatcher") => "Dispatcher",
        _ when name.EndsWith("Metrics") => "Metrics",
        _ when name.EndsWith("Recorder") => "Recorder",
        _ when name.EndsWith("Checker") => "Checker",
        _ when name.EndsWith("Builder") => "Builder",
        _ when name.EndsWith("Provider") => "Provider",
        _ when name.EndsWith("Client") => "HTTP Client",
        _ when name.EndsWith("Handler") => "Handler",
        _ when name.EndsWith("Settings") => "Config",
        _ when name.EndsWith("Definition") => "Config",
        _ when name.EndsWith("Event") => "Event",
        _ when name.EndsWith("Response") => "DTO",
        _ when name.EndsWith("Snapshot") => "Snapshot",
        _ when name.EndsWith("Record") => "DTO",
        _ when name.StartsWith("Base") => "Base type",
        _ => null,
    };

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
