using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Domain;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Core.Systems;

// Своё правило из шаблона (adr/0012): чистая функция над уже посчитанным per-service анализом, как DriftDetector.
// Правило — это данные (CustomRuleDto); совпадения — порядковое сравнение строк и сегментов путей, без регулярных выражений
// (профиль приходит от клиента: ни ReDoS, ни исполнения чужого кода). Тесты (слой tests) не судим: правила — про сервисный код.
public static class CustomRuleDetector
{
    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses, IReadOnlyList<CustomRuleDto>? rules)
    {
        if (rules is not { Count: > 0 }) return [];
        var problems = new List<SystemProblem>();
        foreach (var rule in rules)
        {
            if (!Enum.TryParse<Strictness>(rule.Strictness, ignoreCase: true, out var strictness) || strictness == Strictness.Off) continue;
            var severity = strictness switch { Strictness.Error => ProblemSeverity.Critical, Strictness.Warning => ProblemSeverity.Warning, _ => ProblemSeverity.Info };
            foreach (var (svc, location, result) in analyses)
            {
                if (result is null) continue; // «не разобрался» уже отмечают линзы дрейфа и слоёв
                var items = Check(rule, result);
                if (items.Count == 0) continue;
                problems.Add(new SystemProblem(severity, CustomRuleTemplates.KindPrefix + rule.Id,
                    $"«{svc.Name}»: {rule.Title} — {items.Count}", location, ProblemCategory.Convention, items));
            }
        }
        return problems;
    }

    /// <summary>Нарушения одного правила в одном сервисе.</summary>
    internal static List<ProblemItem> Check(CustomRuleDto rule, AnalysisResult result)
    {
        var types = result.Types.Where(t => !t.Layer.Equals("tests", StringComparison.OrdinalIgnoreCase)).ToList();
        switch (rule.Template)
        {
            case CustomRuleTemplates.SuffixBase:
                return [.. types
                    .Where(t => t.Name.EndsWith(rule.Suffix!, StringComparison.Ordinal) && t.Name != rule.BaseType && !HasBase(t, rule.BaseType!))
                    .Select(t => new ProblemItem(t.Name, t.Layer, $"тип на «{rule.Suffix}» должен наследовать «{rule.BaseType}»", t.File))];

            case CustomRuleTemplates.ForbidInFolder:
                return [.. types
                    .Where(t => InFolder(t.File, rule.Folder!)
                        && (string.IsNullOrWhiteSpace(rule.Suffix) || t.Name.EndsWith(rule.Suffix, StringComparison.Ordinal))
                        && (string.IsNullOrWhiteSpace(rule.BaseType) || HasBase(t, rule.BaseType)))
                    .Select(t => new ProblemItem(t.Name, t.Layer, $"типу не место в папке «{rule.Folder}»", t.File))];

            case CustomRuleTemplates.RoleInFolder:
                return [.. types
                    .Where(t => t.Role.Equals(rule.Role, StringComparison.OrdinalIgnoreCase) && t.File.Length > 0 && !InFolder(t.File, rule.Folder!))
                    .Select(t => new ProblemItem(t.Name, t.Layer, $"роль «{rule.Role}» живёт в папке «{rule.Folder}»", t.File))];

            case CustomRuleTemplates.LayerNoRef:
            {
                var layerOf = LayerGraph.LayerByName(types);
                var fileOf = types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().File, StringComparer.Ordinal);
                return [.. result.Edges
                    .Where(e => layerOf.TryGetValue(e.From, out var fl) && layerOf.TryGetValue(e.To, out var tl)
                        && fl.Equals(rule.FromLayer, StringComparison.OrdinalIgnoreCase) && tl.Equals(rule.ToLayer, StringComparison.OrdinalIgnoreCase))
                    .Select(e => (e.From, e.To)).Distinct()
                    .Select(e => new ProblemItem($"{e.From} → {e.To}", $"{layerOf[e.From]} → {layerOf[e.To]}",
                        $"слой «{rule.FromLayer}» не должен ссылаться на «{rule.ToLayer}»", fileOf.GetValueOrDefault(e.From, "")))];
            }
            default: return [];
        }
    }

    // База совпадает по простому имени; обобщённая («IHandler<X>») считается по имени до «<».
    private static bool HasBase(TypeInfo t, string baseType) =>
        t.Bases.Any(b => b == baseType || (b.StartsWith(baseType, StringComparison.Ordinal) && b.Length > baseType.Length && b[baseType.Length] == '<'));

    /// <summary>Лежит ли файл в папке: сегменты папки идут подряд в каталоге файла (без регистра, «/» и «\» равны).</summary>
    public static bool InFolder(string file, string folder)
    {
        var want = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (want.Length == 0) return false;
        var have = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + want.Length <= have.Length - 1; i++)   // последний сегмент — имя файла, в каталогах он не ищется
        {
            var ok = true;
            for (var j = 0; j < want.Length && ok; j++) ok = have[i + j].Equals(want[j], StringComparison.OrdinalIgnoreCase);
            if (ok) return true;
        }
        return false;
    }
}
