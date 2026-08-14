namespace ArchForge.Contracts;

// Импорты (сторонние библиотеки) сервиса — «за кнопкой», не в графе. Контракт для фронта.
public sealed record ImportsDto(string Project, IReadOnlyList<PackageDto> Packages);

public sealed record PackageDto(
    string Name,
    IReadOnlyList<string> Versions, // разные версии в разных проектах (>1 = конфликт)
    IReadOnlyList<string> UsedBy,   // проекты таргета, ссылающиеся напрямую
    bool VersionConflict);
