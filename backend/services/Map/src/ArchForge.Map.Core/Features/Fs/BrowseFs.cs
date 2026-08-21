using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Fs;

// GET /api/fs?path= — обзор папок на диске сервера (для пикера «Выбрать папку»).
// Инструмент локальный (бэкенд на той же машине), поэтому листинг ФС допустим — как и сам разбор произвольного пути.
// Без path → домашняя папка. path=/ → список дисков (Windows). Возвращает подпапки + путь к родителю (для «вверх»).
public sealed class BrowseFsEndpoint : IEndpoint
{
    public sealed record Entry(string Name, string Path, bool Project);
    public sealed record Listing(string Path, string? Parent, IReadOnlyList<Entry> Entries, IReadOnlyList<string> Drives);

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/fs", (string? path) =>
        {
            string root = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : path.Replace('\\', '/');

            // Корень: на Windows — список дисков; на *nix «/» — обычная папка (ниже).
            if (root == "/" && OperatingSystem.IsWindows())
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => d.Name.Replace('\\', '/'))
                    .ToList();
                return Results.Ok(new Listing("/", null, Array.Empty<Entry>(), drives));
            }

            if (!Directory.Exists(root))
                return Results.BadRequest(new { error = $"Папка не найдена: {root}" });

            List<Entry> entries;
            try
            {
                entries = Directory.EnumerateDirectories(root)
                    .Where(d => !IsSkipped(d))
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .Select(d => new Entry(Path.GetFileName(d), d.Replace('\\', '/'), LooksLikeProject(d)))
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = $"Нет доступа к папке: {root}" });
            }

            string? parent = Directory.GetParent(root)?.FullName?.Replace('\\', '/');
            // На корне диска (C:/) родителя нет → «/», чтобы вернуться к списку дисков (Windows).
            if (parent is null && OperatingSystem.IsWindows()) parent = "/";

            return Results.Ok(new Listing(root.Replace('\\', '/'), parent, entries, Array.Empty<string>()));
        });

    // Прячем скрытые/системные и «шумные» служебные папки.
    private static bool IsSkipped(string dir)
    {
        string name = Path.GetFileName(dir);
        if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") return true;
        try { return (File.GetAttributes(dir) & FileAttributes.Hidden) != 0; }
        catch { return true; }
    }

    // Подсказка пикеру: папка похожа на корень системы/сервиса (есть .sln / compose / src).
    private static bool LooksLikeProject(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.sln").Any()
                || Directory.EnumerateFiles(dir, "docker-compose*.y*ml").Any()
                || Directory.Exists(Path.Combine(dir, "src"));
        }
        catch { return false; }
    }
}
