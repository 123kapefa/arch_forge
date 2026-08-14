namespace ArchForge.Analysis.Core;

// Порт: прямые ссылки на библиотеки по пути к сервису (из .csproj). Реализация — в Infrastructure.
public interface IImportSource
{
    IReadOnlyList<PackageRef> Read(string path);
}
