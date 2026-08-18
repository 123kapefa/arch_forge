using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Fs;

// GET /api/git?path= — текущая git-ветка открытого проекта (для статус-бара).
// Читаем .git/HEAD (без вызова git): «ref: refs/heads/<branch>» → branch; detached/нет репо → null.
// Ищем .git вверх по дереву (скан-корень может быть подпапкой репозитория). Поддержан .git-файл (worktree/submodule).
public sealed class GitBranchEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/git", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.Ok(new { branch = (string?)null });
            return Results.Ok(new { branch = BranchOf(path) });
        });

    private static string? BranchOf(string root)
    {
        try
        {
            var head = FindHeadFile(root);
            if (head is null || !File.Exists(head)) return null;
            var content = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return content.StartsWith(prefix, StringComparison.Ordinal) ? content[prefix.Length..] : null; // detached → null
        }
        catch { return null; }
    }

    // .git может быть папкой ИЛИ файлом «gitdir: <path>» (worktree/submodule). Ищем вверх по дереву.
    private static string? FindHeadFile(string root)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(root));
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git)) return Path.Combine(git, "HEAD");
            if (File.Exists(git))
            {
                var line = File.ReadAllText(git).Trim();
                if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var gd = line["gitdir:".Length..].Trim();
                    if (!Path.IsPathRooted(gd)) gd = Path.GetFullPath(Path.Combine(dir.FullName, gd));
                    return Path.Combine(gd, "HEAD");
                }
                return null;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
