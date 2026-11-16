namespace ArchForge.Analysis.Core;

/// <summary>Типы сервиса + как они получены. <paramref name="SyntaxOnly"/> = true: семантика не получилась (сбой Roslyn, пустой результат,
/// слишком много ссылок), типы взяты синтаксическим полом — базы только прямые, связей из тела метода нет, роли и покрытие занижены (adr/0008).</summary>
public sealed record TypeScan(IReadOnlyList<RawType> Types, bool SyntaxOnly);

/// <summary>Источник типов, который отдаёт статус семантики вместе с типами (одним результатом, без чтения «потом» из кеша).
/// Обычный <see cref="ITypeSource"/> остаётся как был; анализатор берёт этот интерфейс, если источник его реализует.</summary>
public interface IScannedTypeSource : ITypeSource
{
    TypeScan ReadScan(string path, CancellationToken ct = default);
}
