namespace ArchForge.Analysis.Core;

// Сырой тип из разбора исходников (до классификации). Отдаёт источник (порт ITypeSource).
public sealed record RawType(
    string Name,
    string Kind,
    IReadOnlyList<string> Bases,
    string CtorParams, // текст списка параметров первичного конструктора (для uses/holds)
    string Layer,
    string File);
