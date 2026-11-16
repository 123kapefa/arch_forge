namespace ArchForge.Analysis.Core;

// Исходящая ссылка типа на другой тип — для рёбер графа. Kind: uses | holds | raises | handles | refs (слабый).
public sealed record Ref(string Name, string Kind);

// Член типа (для L3: тип → члены). Kind: endpoint | method | property | field.
public sealed record RawMember(
    string Name,
    string Kind,
    string? Signature = null,  // напр. «(Guid id) : Task<Product>» для метода, «string» для свойства
    string? HttpMethod = null, // GET|POST|... для endpoint
    string? Route = null,      // маршрут endpoint (как в атрибуте)
    int? Line = null);

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

    // Синтаксический пол: ВСЕ простые имена, встреченные в теле/сигнатурах типа. BuildEdges оставит только имена своих
    // типов и сделает из них слабые refs (по именам, без семантики — возможны совпадения с одноимёнными членами).
    public IReadOnlyList<string> WeakNames { get; init; } = [];

    // Члены типа (L3). Заполняет семантический источник; у синтаксического пола пусто.
    public IReadOnlyList<RawMember> Members { get; init; } = [];
}
