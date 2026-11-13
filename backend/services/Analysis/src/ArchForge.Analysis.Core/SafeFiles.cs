using System.IO.Enumeration;

namespace ArchForge.Analysis.Core;

// Единый безопасный обход файлов ТАРГЕТА (недоверенного проекта). Символические ссылки (и точки повторной обработки) не обходим и не
// отдаём: ссылка внутри проекта на папку/файл вне него читала бы чужой код и конфигурацию, а петля ссылок раскручивалась бы многократно
// (#158). Корень, который пользователь сам указал ссылкой, работает: ссылка проверяется у ЗАПИСЕЙ внутри корня, не у самого корня.
//
// • skipHidden — как прежний `new EnumerationOptions()` (Hidden|System): не заходить в каталоги и не отдавать файлы, скрытые ФС
//   (на Unix — имя с точки: .git, .claude…). Включают обходы, где так было раньше (discovery, compose, appsettings): скрытые копии
//   проекта (.claude/worktrees) там не нужны, а обходить их дорого. Остальные обходы скрытое видели и видят (PathFilter отсекает позже).
// • Облачные файлы-заглушки Windows (OneDrive Files On-Demand) тоже помечены ReparsePoint, но это не ссылки: их не пропускаем.
// • Регистр маски — как у ОС (PlatformDefault): на Linux учитывается, на Windows/macOS нет.
// • Глубина ограничена (защита от петель и враждебных деревьев); глубже — файлы не видны.
public static class SafeFiles
{
    public const int MaxDepth = 128;

    // биты FileAttributes без имён в enum: RecallOnOpen (0x40000) и RecallOnDataAccess (0x400000) — облачные заглушки
    private const FileAttributes CloudPlaceholder = (FileAttributes)(0x40000 | 0x400000);

    /// <summary>Файлы под <paramref name="root"/> по маске (`*.cs`, `*compose*`), рекурсивно или только в самой папке.</summary>
    public static IEnumerable<string> Enumerate(string root, string pattern, bool recurse = true, bool skipHidden = false)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = skipHidden ? FileAttributes.Hidden | FileAttributes.System : 0,
            MaxRecursionDepth = MaxDepth,
        };
        var ignoreCase = !OperatingSystem.IsLinux();
        return new FileSystemEnumerable<string>(root, (ref FileSystemEntry e) => e.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && !IsLink(e.Attributes)
                && FileSystemName.MatchesSimpleExpression(pattern, e.FileName, ignoreCase),
            ShouldRecursePredicate = (ref FileSystemEntry e) => !IsLink(e.Attributes),
        };
    }

    /// <summary>Полный путь, если <paramref name="path"/> лежит ВНУТРИ <paramref name="root"/> и ни один компонент от корня (не включая) до самого пути
    /// не символическая ссылка; иначе null (в том числе если пути нет). Для ПРЯМЫХ чтений по путям из недоверенного проекта (env_file, ProjectReference,
    /// Directory.Packages.props): проверка «лежит под корнем» только по строке не ловит ссылку в середине пути.</summary>
    public static string? ResolveInside(string root, string path)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd('/', '\\');
            var full = Path.GetFullPath(path);
            var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (r.Length == 0 || !full.StartsWith(r + Path.DirectorySeparatorChar, cmp)) return null;
            var cur = r;
            foreach (var seg in full[(r.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                cur = Path.Combine(cur, seg);
                FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
                if (!info.Exists || IsLink(info.Attributes)) return null;
            }
            return full;
        }
        catch { return null; }
    }

    /// <summary>Файл — не символическая ссылка (для чтения известных имён: archforge.conventions.json).</summary>
    public static bool IsRegularFile(string file)
    {
        try { var fi = new FileInfo(file); return fi.Exists && !IsLink(fi.Attributes); }
        catch { return false; }
    }

    // Ссылка = точка повторной обработки, кроме облачных заглушек. На Unix символическая ссылка получает ReparsePoint.
    private static bool IsLink(FileAttributes a) => (a & FileAttributes.ReparsePoint) != 0 && (a & CloudPlaceholder) == 0;
}
