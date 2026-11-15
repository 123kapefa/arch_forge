using ArchForge.Analysis.Core.Profiles;

namespace ArchForge.Analysis.Core.Systems;

// Одно решение (.sln) + один compose с несколькими сервисами (KalmykovGroup.Server: десяток микросервисов в src/Services/*).
// Раньше такое решение разбиралось ЦЕЛИКОМ как один сервис: дрейф и слои падали в общую кучу «на всю солюшн».
// Здесь сопоставляем compose-сервисы с ПАПКАМИ проектов по имени (путь, не гадание — adr/0008):
//  • кандидаты — папки с .csproj внутри решения и их предки (src/Services/Catalog для Catalog.API/Catalog.Domain/…);
//  • ищем от верхних уровней к нижним: узел, привязанный к папке выше, не ищется глубже (Catalog, а не Catalog.API);
//  • само сопоставление имя↔узел — ServiceNodeMatcher (точное имя или стем как целые слова; сателлиты не берём).
// Решение заменяется папками, только если привязаны ≥2 сервиса: одно совпадение — слабое свидетельство, оставляем как было.
// Код решения вне привязанных папок (Shared, неопознанные сервисы) в дрейф не попадает — это сказано в документации.
public static class ProjectFolderSplitter
{
    private const int MaxDepth = 4;          // глубже src/Services/X/X.API/... кандидатов не ищем
    private const int MaxProjects = 5000;    // защита от враждебного дерева: больше .csproj не перебираем

    public static IReadOnlyList<(DiscoveredService Svc, SystemNode Node)> Split(
        string root, DiscoveredService umbrella, IReadOnlyList<SystemNode> nodes, ServiceNaming naming,
        IReadOnlySet<string> otherServiceRoots, CancellationToken ct = default)
    {
        var location = SystemNamespace.Of(root, umbrella.Root);
        var own = nodes.Where(n => n.Kind == SystemNodeKind.Service && !naming.IsSatellite(n.Name)
                                   && HttpTargetResolver.SystemOf(n.Id) == location).ToList();
        if (own.Count < 2) return [];                       // в compose решения один сервис — делить нечего

        var dirs = CandidateDirs(umbrella.Root, otherServiceRoots, ct);
        var bound = new List<(DiscoveredService Svc, SystemNode Node)>();
        var free = own;
        var claimed = new List<string>();                   // уже привязанные папки: вложенные в них не берём
        for (var depth = 1; depth <= MaxDepth && free.Count > 0; depth++)
        {
            var level = dirs.Where(d => Depth(umbrella.Root, d) == depth && !claimed.Any(c => Inside(c, d)))
                            .Select(d => new DiscoveredService(Path.GetFileName(d), d, umbrella.ComposePath)).ToList();
            if (level.Count == 0) continue;
            foreach (var (svc, node) in ServiceNodeMatcher.Match(level, free, naming))
            {
                bound.Add((svc, node));
                claimed.Add(svc.Root);
            }
            var taken = bound.Select(b => b.Node.Id).ToHashSet(StringComparer.Ordinal);
            free = free.Where(n => !taken.Contains(n.Id)).ToList();
        }
        return bound.Count >= 2 ? bound.OrderBy(b => b.Svc.Root, StringComparer.Ordinal).ToList() : [];
    }

    // Папки решения глубиной ≤MaxDepth, в которых (или под которыми) есть .csproj; без шума и без корней других сервисов.
    private static List<string> CandidateDirs(string slnRoot, IReadOnlySet<string> otherServiceRoots, CancellationToken ct)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var seen = 0;
        foreach (var proj in SafeFiles.Enumerate(slnRoot, "*.csproj", skipHidden: true))
        {
            ct.ThrowIfCancellationRequested();
            if (++seen > MaxProjects) break;
            if (PathFilter.IsNoise(slnRoot, proj)) continue;
            var dir = Path.GetDirectoryName(proj);
            while (!string.IsNullOrEmpty(dir) && Depth(slnRoot, dir) > 0)
            {
                if (Depth(slnRoot, dir) <= MaxDepth && !otherServiceRoots.Contains(dir)) set.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        return [.. set];
    }

    private static int Depth(string root, string dir) =>
        SystemNamespace.Of(root, dir).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool Inside(string parent, string child) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
