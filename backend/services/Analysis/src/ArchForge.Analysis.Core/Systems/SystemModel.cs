namespace ArchForge.Analysis.Core.Systems;

// Модель СИСТЕМНОГО уровня (Фаза 1.5): сервисы + инфра + связи + проблемы.
// Спайк P1.5-0 — облегчённая форма; в P1.5-A это ляжет в аддитивный контракт GraphDto.

public enum SystemNodeKind { Service, Datastore, Topic, External }

/// <summary>Источник связи (провенанс, честность adr/0008).</summary>
public enum EdgeVia { Compose, Config, Code, Unknown }

public enum ProblemSeverity { Critical, Warning, Coverage }

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

/// <summary>Обнаруженная проблема с провенансом и местом.</summary>
public sealed record SystemProblem(ProblemSeverity Severity, string Kind, string Title, string Location);

public sealed record SystemGraph(
    IReadOnlyList<SystemNode> Nodes,
    IReadOnlyList<SystemEdge> Edges,
    IReadOnlyList<SystemProblem> Problems);
