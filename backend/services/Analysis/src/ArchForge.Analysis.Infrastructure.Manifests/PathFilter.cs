namespace ArchForge.Analysis.Infrastructure.Manifests;

// Единый фильтр шума для discovery. Проверяем путь ОТНОСИТЕЛЬНО корня системы, а не абсолютный —
// иначе имя папки-предка вне воркспейса (напр. ~/Downloads/backup/…) ложно отсеяло бы всё (ревью P1.5-B).
internal static class PathFilter
{
    private static readonly string[] Noise =
        { "/.claude/", "worktrees", "backup", "dbdumps", "/node_modules/", "/bin/", "/obj/" };

    public static bool IsNoise(string root, string path)
    {
        var rel = "/" + Path.GetRelativePath(root, path).Replace('\\', '/');
        foreach (var needle in Noise)
            if (rel.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
