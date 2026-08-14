namespace ArchForge.Analysis.Core;

// Прямая ссылка проекта на библиотеку (из <PackageReference> в .csproj). Агрегируется в ImportsDto.
public sealed record PackageRef(string Name, string Version, string Project);
