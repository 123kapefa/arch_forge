namespace ArchForge.Contracts;

// Язык-агностичный контракт графа архитектуры (IR). Общий для Analysis, Map и фронта.
// Форма ориентирована на вьюер: у узлов Id, у рёбер Source/Target (как ждёт React Flow).

public sealed record GraphDto(
    string Project,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<EdgeDto> Edges,
    CoverageDto Coverage,
    IReadOnlyList<string> Drift);

public sealed record NodeDto(
    string Id,
    string Name,
    string Kind,   // class | record | struct | interface | enum
    string Layer,
    string Role,
    string Via,    // marker | heuristic | unknown
    string File,
    IReadOnlyList<string> Drift);

public sealed record EdgeDto(
    string Id,
    string Source, // Node.Id
    string Target, // Node.Id
    string Kind);  // implements | holds | uses

public sealed record CoverageDto(int Marker, int Heuristic, int Unknown, int Total);
