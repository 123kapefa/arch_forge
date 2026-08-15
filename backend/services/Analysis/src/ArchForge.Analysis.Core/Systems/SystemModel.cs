namespace ArchForge.Analysis.Core.Systems;

// Модель СИСТЕМНОГО уровня (Фаза 1.5): сервисы + инфра + связи + проблемы.
// Спайк P1.5-0 — облегчённая форма; в P1.5-A это ляжет в аддитивный контракт GraphDto.

public enum SystemNodeKind { Service, Datastore, Topic, External }

/// <summary>Источник связи (провенанс, честность adr/0008).</summary>
public enum EdgeVia { Compose, Config, Code, Unknown }

// Critical/Warning/Coverage — по возрастанию мягкости; Info — информативно (не влияет на health, линза «дрейф»).
public enum ProblemSeverity { Critical, Warning, Coverage, Info }

/// <summary>Линза проблемы: универсальная арх-проблема vs соответствие своду (див. две линзы, adr/0008).</summary>
public static class ProblemCategory
{
    public const string Architecture = "architecture"; // свод-независимо (общая БД, цикл, висячее событие)
    public const string Convention = "convention";      // отклонение от свода (дрейф) — только когда запрошено
}

/// <summary>Узел системы: сервис / хранилище / топик / внешний (отложенный) узел.</summary>
public sealed record SystemNode(string Id, string Name, SystemNodeKind Kind, string? Image = null);

/// <summary>
/// Ребро системного уровня. <paramref name="Confidence"/> 0..1 (по источнику);
/// <paramref name="Deferred"/> = target/значение отложено (`${KEY}` придёт при деплое).
/// </summary>
public sealed record SystemEdge(
    string Source,
    string Target,
    string Kind,
    EdgeVia Via,
    double Confidence,
    bool Deferred = false);

/// <summary>Обнаруженная проблема с провенансом, местом и линзой (по умолчанию — арх-проблема).</summary>
public sealed record SystemProblem(
    ProblemSeverity Severity, string Kind, string Title, string Location,
    string Category = ProblemCategory.Architecture);

public sealed record SystemGraph(
    IReadOnlyList<SystemNode> Nodes,
    IReadOnlyList<SystemEdge> Edges,
    IReadOnlyList<SystemProblem> Problems);
