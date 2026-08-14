namespace ArchForge.Analysis.Core;

// Исходящая ссылка типа на другой тип — для рёбер графа. Kind: uses | holds | raises | handles.
public sealed record Ref(string Name, string Kind);

// Сырой тип из разбора исходников (до классификации). Отдаёт источник (порт ITypeSource).
public sealed record RawType(
    string Name,
    string Kind,
    IReadOnlyList<string> Bases,
    string CtorParams, // текст параметров первичного ctor — fallback для uses/holds у синтаксического пола
    string Layer,
    string File)
{
    // Разрешённые семантикой исходящие ссылки. Пусто у пола → BuildEdges падает на CtorParams-регекс.
    public IReadOnlyList<Ref> Refs { get; init; } = [];
}
