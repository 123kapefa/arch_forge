using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Core.Systems;

// Свод-линза «маппинг не централизован» (convention, мягко): по оси конструирования ловит data-DTO, который
// собирается через new в ≥N местах ВНЕ мапперов и в ≥M файлах — значит entity→DTO размазан по хендлерам
// (добавил поле → правишь везде). Не архитектура, а стиль → info, не красит health (adr/0008).
// Пороги/суффиксы пока в коде; следующий шаг — вынести в свод (ConventionPack).
public static class DtoMappingDetector
{
    // места, где инлайн-сборка DTO — норма: проводка (DI/Program). Мапперы задаёт свод (rule.CentralizeIn).
    private static bool InWiring(ConstructionSite s) =>
        s.File.Replace('\\', '/').EndsWith("/Program.cs")
        || s.InType.EndsWith("DependencyInjection") || s.InType.EndsWith("Startup")
        || s.InType.EndsWith("Module") || s.InType.EndsWith("Registration");

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ConstructionSite> sites, string location, DtoMappingRule rule)
    {
        bool IsDto(string t) => rule.Suffix.Any(suf => t.EndsWith(suf, StringComparison.Ordinal));
        bool InCentralizer(string t) => rule.CentralizeIn.Any(suf => t.EndsWith(suf, StringComparison.Ordinal));

        var candidates = sites.Where(s => IsDto(s.TypeName) && s.Layer != "tests" && !InWiring(s) && !InCentralizer(s.InType));

        var problems = new List<SystemProblem>();
        foreach (var g in candidates.GroupBy(s => s.TypeName, StringComparer.Ordinal))
        {
            var group = g.ToList();
            var files = group.Select(s => s.File).Distinct(StringComparer.Ordinal).Count();
            if (group.Count < rule.MinSites || files < rule.MinFiles) continue; // одиночное/локальное — не смелл

            var items = group
                .OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line)
                .Select(s => new ProblemItem(
                    Name: s.InType,                                  // где собирается (тип-хендлер) — переход к его плашке
                    Layer: s.Layer,
                    Note: string.IsNullOrEmpty(s.InMethod) ? "инлайн" : s.InMethod,
                    File: s.File, Line: s.Line))
                .ToList();

            problems.Add(new SystemProblem(
                ProblemSeverity.Info, "dto-mapping",
                $"«{g.Key}»: собирается в {files} местах вне мапперов — маппинг не централизован",
                location, ProblemCategory.Convention, items));
        }

        return problems.OrderByDescending(p => p.Items!.Count).ToList();
    }
}
