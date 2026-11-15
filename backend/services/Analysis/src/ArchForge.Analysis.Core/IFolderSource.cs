namespace ArchForge.Analysis.Core;

// Порт: раскладка кода по папкам и именам файлов (методичка «Структура папок»: раскладывай по сущности, а не по тех. признаку).
// Разбор кода не нужен — только пути и имена файлов; реализация — в Infrastructure.Roslyn (общий безопасный обход файлов).
public interface IFolderSource
{
    FolderFacts Read(string root, CancellationToken ct = default);
}

/// <param name="Name">Папка (путь от корня сервиса) или имя файла без расширения.</param>
/// <param name="File">Пример файла внутри (для папки) или сам файл.</param>
public sealed record FolderSite(string Name, string File, string Layer);

public sealed record FolderFacts(IReadOnlyList<FolderSite> TechFolders, IReadOnlyList<FolderSite> TechNames)
{
    public static FolderFacts Empty { get; } = new([], []);
}
