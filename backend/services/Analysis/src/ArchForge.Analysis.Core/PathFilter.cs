namespace ArchForge.Analysis.Core;

// Единый фильтр шума при обходе файлов таргета. Путь проверяем ОТНОСИТЕЛЬНО корня системы, а не абсолютный —
// иначе имя папки-предка вне воркспейса (напр. ~/Downloads/backup/…) ложно отсеяло бы всё (ревью P1.5-B).
//
// Две политики:
//  • IsHiddenOrCopy — скрытые/служебные каталоги (.git/.claude/.vs/.idea) и копии/дампы (worktrees/backup/
//    dbdumps/node_modules). Исключать ВЕЗДЕ: копия исходников (git-worktree под .claude/) разбирается наравне
//    с оригиналом → дубли типов/рёбер/эндпоинтов и неоднозначные символы (ломается резолв route-констант).
//  • IsNoise — то же ПЛЮС сборочный вывод (bin/obj). Для обхода ИСХОДНИКОВ и discovery. Для reference-сборок
//    bin/ нужен, поэтому там берут только IsHiddenOrCopy (исключить копию, оставить настоящий bin/).
public static class PathFilter
{
    private static readonly string[] CopyNoise = { "worktrees", "backup", "dbdumps", "/node_modules/" };
    private static readonly string[] BuildOutput = { "/bin/", "/obj/" };

    /// <summary>Скрытый/служебный каталог или копия исходников — исключаем в любом сканере.</summary>
    public static bool IsHiddenOrCopy(string root, string path)
    {
        var rel = Rel(root, path);
        if (rel.Contains("/.", StringComparison.Ordinal)) return true; // сегмент/файл с точки (.git/.claude/…)
        foreach (var needle in CopyNoise)
            if (rel.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Полный шум (копии/скрытое + сборочный вывод) — для обхода исходников и discovery.</summary>
    public static bool IsNoise(string root, string path)
    {
        if (IsHiddenOrCopy(root, path)) return true;
        var rel = Rel(root, path);
        foreach (var needle in BuildOutput)
            if (rel.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Rel(string root, string path) => "/" + Path.GetRelativePath(root, path).Replace('\\', '/');
}
