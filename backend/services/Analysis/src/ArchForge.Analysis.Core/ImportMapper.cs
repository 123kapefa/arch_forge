using ArchForge.Contracts;

namespace ArchForge.Analysis.Core;

// Агрегат прямых PackageRef → ImportsDto: по библиотеке — версии, кто использует, конфликт версий.
// Конфликты и часто используемые — выше в списке.
public static class ImportMapper
{
    public static ImportsDto ToDto(IReadOnlyList<PackageRef> refs, string project)
    {
        var packages = refs
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var versions = g.Select(r => r.Version)
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var usedBy = g.Select(r => r.Project)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new PackageDto(g.Key, versions, usedBy, versions.Count > 1);
            })
            .OrderByDescending(p => p.VersionConflict)
            .ThenByDescending(p => p.UsedBy.Count)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ImportsDto(project, packages);
    }
}
