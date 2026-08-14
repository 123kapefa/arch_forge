namespace ArchForge.Analysis.Domain;

// Модель графа архитектуры (результат разбора). Язык-агностичная, отдаётся как IR.

public sealed record TypeInfo(
    string Name,
    string Kind, // class | record | struct | interface | enum
    string Layer,
    string Role,
    string Via, // marker | heuristic | unknown
    IReadOnlyList<string> Bases,
    string File,
    IReadOnlyList<string> Drift);

public sealed record TypeEdge(string From, string To, string Kind); // implements | holds | uses | raises | handles

public sealed record Coverage(int Marker, int Heuristic, int Unknown);

public sealed record AnalysisResult(
    IReadOnlyList<TypeInfo> Types,
    IReadOnlyList<TypeEdge> Edges,
    Coverage Coverage,
    IReadOnlyList<string> Drift);
