namespace ArchForge.Analysis.Core.Systems;

// Модель СИСТЕМНОГО уровня (Фаза 1.5): сервисы + инфра + связи + проблемы.
// Спайк P1.5-0 — облегчённая форма; в P1.5-A это ляжет в аддитивный контракт GraphDto.

public enum SystemNodeKind { Service, Datastore, Topic, External, Shared }

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

/// <summary>Публикация порта (E9.1): контейнерный порт, хост-порт (если опубликован наружу), bind-IP.
/// <c>Container</c>/<c>Host</c> — «сырые» строки из compose (могут быть диапазоном "8080-8090" или переменной),
/// не гарантированно единичный int; детектор публичности учитывает это.</summary>
/// <param name="Published">Опубликован на хост (доступен снаружи контейнера). <c>HostIp</c>=null → все интерфейсы (0.0.0.0).</param>
public sealed record PortMapping(string Container, string? Host = null, string? HostIp = null, bool Published = false);

/// <summary>DevOps-атрибуты сервиса из compose (E9.1): порты, наличие healthcheck, сети.</summary>
public sealed record DeployInfo(
    IReadOnlyList<PortMapping> Ports,
    bool HasHealthcheck,
    IReadOnlyList<string> Networks);

/// <summary>Узел системы: сервис / хранилище / топик / внешний (отложенный) узел.</summary>
public sealed record SystemNode(string Id, string Name, SystemNodeKind Kind, string? Image = null, DeployInfo? Deploy = null);

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
    string Category = ProblemCategory.Architecture,
    IReadOnlyList<ProblemItem>? Items = null); // отдельные факты (типы дрейфа / пары нарушений) для раскрытия

// Отдельный факт агрегированной проблемы: имя (тип / «From → To»), слой, короткая заметка.
// File/Line — где живёт тип (переход «к файлу/плашке»); File пуст, если источник не дал путь.
public sealed record ProblemItem(string Name, string Layer, string Note, string File = "", int? Line = null);

/// <summary>Среда развёртывания (E9.1 слой 2): имя (default / dev / stage / prod…) + активна ли она сейчас.
/// «default» = base (+ docker-compose.override.yml); именованные = base + docker-compose.&lt;env&gt;.yml.</summary>
public sealed record SystemEnvironment(string Name, bool Active);

public sealed record SystemGraph(
    IReadOnlyList<SystemNode> Nodes,
    IReadOnlyList<SystemEdge> Edges,
    IReadOnlyList<SystemProblem> Problems,
    // E9.1 слой 2: доступные среды (из override/named compose-файлов). null → выбора сред нет (одна base).
    IReadOnlyList<SystemEnvironment>? Environments = null);
