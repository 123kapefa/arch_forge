namespace ArchForge.Analysis.Core.Systems;

// Правило «папки по сущности, а не по тех. признаку» (методичка «Структура папок»): папки Common/Utils/Helpers/Services/… не заводят,
// Commands/Queries — только внутри Features/{Entity}; слова Manager/Helper/Resolver/Processor/Util в имени — повод переименовать.
// Находка уровня сервиса, мягкая (Info, «свод», adr/0008). Чистая функция над IFolderSource.
public static class FolderStructureDetector
{
    public const string Kind = "folder-structure";
    public const int MaxItems = 1000;

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var sa in analyses)
        {
            var folders = sa.Folders.TechFolders.Where(s => !IsTests(s)).ToList();
            var names = sa.Folders.TechNames.Where(s => !IsTests(s)).ToList();
            if (folders.Count == 0 && names.Count == 0) continue;
            var items = folders.Select(s => new ProblemItem(s.Name + "/", s.Layer, "папка названа по тех. признаку, а не по сущности: разложите содержимое по Features/{Entity}/ или Domain/{Entity}/", s.File))
                .Concat(names.Select(s => new ProblemItem(s.Name, s.Layer, "имя «чем является», а не «что делает»: переименуйте глаголом (FindOrCreateFounder) и положите в папку владельца", s.File)))
                .Take(MaxItems).ToList();
            var parts = new List<string>();
            if (folders.Count > 0) parts.Add($"{folders.Count} папок с тех. названием");
            if (names.Count > 0) parts.Add($"{names.Count} файлов вида Manager/Helper/Resolver");
            var more = folders.Count + names.Count > MaxItems ? $" (показано {MaxItems})" : "";
            problems.Add(new SystemProblem(ProblemSeverity.Info, Kind,
                $"«{sa.Service.Name}»: раскладка не по сущности — {string.Join(", ", parts)}{more}",
                sa.NodeId ?? sa.Location, ProblemCategory.Convention, items));
        }
        return problems;
    }

    private static bool IsTests(FolderSite s) => s.Layer.Equals("tests", StringComparison.OrdinalIgnoreCase);
}
