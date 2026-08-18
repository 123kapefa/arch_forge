using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Проекции системного IR (SystemGraph) в аддитивный контракт GraphDto — экстеншенами по source→DTO,
// чтобы сборка читалась как «g.Nodes.Select(n => n.ToNodeDto(...))», а новое поле правилось в одном месте.
public static class SystemGraphMapping
{
    /// <summary>SystemGraph → GraphDto: узлы/рёбра/проблемы/свод + rollup здоровья системы.</summary>
    public static GraphDto ToGraphDto(this SystemGraph g, string project,
        IReadOnlyDictionary<string, MicroserviceLayerBuilder.Entry>? nodeLayers = null,
        ConventionPack? pack = null)
    {
        // здоровье узла — по самой суровой проблеме, где он location (Critical=0 < Warning=1 < Coverage=2)
        var worstByLocation = g.Problems
            .GroupBy(p => p.Location, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(p => (int)p.Severity), StringComparer.Ordinal);

        var nodes = g.Nodes.Select(n => n.ToNodeDto(worstByLocation, nodeLayers)).ToList();
        var edges = g.Edges.Select(e => e.ToEdgeDto()).ToList();
        var problems = g.Problems.Select((p, i) => p.ToProblemDto(i)).ToList();

        var health = g.Problems.Any(p => p.Severity == ProblemSeverity.Critical) ? "critical"
            : g.Problems.Any(p => p.Severity == ProblemSeverity.Warning) ? "warning"
            : "ok";

        // TODO(P1.5-G): системное покрытие — заглушка (total=узлы); настоящая агрегация per-service — позже.
        return new GraphDto(project, nodes, edges,
            new CoverageDto(0, 0, 0, nodes.Count), Drift: [],
            Health: health,
            Problems: problems.Count > 0 ? problems : null,
            Conventions: pack?.ToConventionsDto());
    }

    /// <summary>Системный узел → NodeDto: вид/уровень + здоровье (по проблемам и слоям) + слои микросервиса.</summary>
    public static NodeDto ToNodeDto(this SystemNode n,
        IReadOnlyDictionary<string, int> worstByLocation,
        IReadOnlyDictionary<string, MicroserviceLayerBuilder.Entry>? nodeLayers)
    {
        var problemHealth = worstByLocation.TryGetValue(n.Id, out var sev) ? HealthOf(sev) : "ok";
        // слои микросервиса (L0: «внутри сервиса — слои») + их здоровье учитываем в health узла
        var layers = nodeLayers is not null && nodeLayers.TryGetValue(
            MicroserviceLayerBuilder.NodeKey(n.Id, n.Name), out var entry) ? entry : null;
        var health = layers is null ? problemHealth : Worse(problemHealth, layers.Health);
        return new NodeDto(n.Id, n.Name, KindString(n.Kind), Layer: "", Role: "", Via: "", File: "", Drift: [],
            Level: LevelOf(n.Kind), Health: health, Layers: layers?.Layers, Root: layers?.Root);
    }

    /// <summary>Системное ребро → EdgeDto (id, провенанс Via, уверенность).</summary>
    public static EdgeDto ToEdgeDto(this SystemEdge e) => new(
        $"{e.Source}-{e.Kind}->{e.Target}", e.Source, e.Target, e.Kind,
        Via: ViaString(e.Via), Confidence: e.Confidence);

    /// <summary>Системная проблема → ProblemDto (id «p{index}», категория-линза, факты, подсказка «как чинить»).</summary>
    public static ProblemDto ToProblemDto(this SystemProblem p, int index) => new(
        $"p{index}", SeverityString(p.Severity), p.Kind, p.Title, p.Location,
        Category: p.Category,
        Items: p.Items?.Select(it => it.ToProblemItemDto()).ToList(),
        Fix: FixFor(p.Kind));

    // Совет «как чинить» — по виду проблемы (одинаков для всех её случаев); неизвестный вид → без подсказки.
    private static string? FixFor(string kind) => kind switch
    {
        "shared-store" or "shared-db" => "Данные чужого сервиса — только через его API/события; заведите своё хранилище на сервис.",
        "service-cycle" => "Разорвите цикл: замените синхронный вызов событием или вынесите общую логику в отдельный сервис.",
        "dangling-event" => "Событие публикуется, но подписчиков нет — добавьте потребителя или уберите публикацию.",
        "orphaned-subscription" => "Подписка без издателя — проверьте имя события или уберите подписку.",
        "isolated-topic" => "Топик без связей — подключите издателя/подписчика или удалите его.",
        "layer-violation" => "Зависимость наружу запрещена — инвертируйте её через интерфейс во внутреннем слое.",
        "dto-mapping" => "Вынесите сборку DTO в один маппер/фабрику (см. centralizeIn в своде).",
        "drift" => "Приведите тип к своду (нужный маркер/суффикс) или дополните archforge.conventions.json.",
        "drift-scan-failed" or "layer-scan-failed" => "Разбор сервиса не удался — проверьте, что он собирается (restore/build).",
        _ => null,
    };

    /// <summary>Отдельный факт проблемы → ProblemItemDto (пустой File → null).</summary>
    public static ProblemItemDto ToProblemItemDto(this ProblemItem it) => new(
        it.Name, it.Layer, it.Note, string.IsNullOrEmpty(it.File) ? null : it.File, it.Line);

    /// <summary>Применённый свод → ConventionsDto (маркеры/fallback отсортированы для стабильности).</summary>
    public static ConventionsDto ToConventionsDto(this ConventionPack pack) => new(
        pack.Source,
        pack.Markers.Select(m => new ConvEntryDto(m.Key, m.Value)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
        pack.Fallback.Select(m => new ConvEntryDto(m.Key, m.Value)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
        pack.Expect.Select(r => new ConvRuleDto(r.Suffix, r.Marker, r.Role)).ToList(),
        new DtoMappingDto(pack.DtoMapping.Suffix, pack.DtoMapping.CentralizeIn, pack.DtoMapping.MinSites, pack.DtoMapping.MinFiles));

    // --- строковые проекции enum'ов + свёртка здоровья ---
    private static string KindString(SystemNodeKind k) => k switch
    {
        SystemNodeKind.Service => "service",
        SystemNodeKind.Datastore => "datastore",
        SystemNodeKind.Topic => "topic",
        SystemNodeKind.External => "external",
        SystemNodeKind.Shared => "shared",
        _ => "unknown",
    };

    // service и shared — с раскрываемыми слоями (drill); инфра — системный уровень
    private static string LevelOf(SystemNodeKind k) => k is SystemNodeKind.Service or SystemNodeKind.Shared ? "service" : "system";

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
