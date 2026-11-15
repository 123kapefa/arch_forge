namespace ArchForge.Analysis.Core.Systems;

// Правило «двойная валидация» (методичка «Валидация — один раз»): у каждого правила один владелец. Поле, у которого есть Value Object,
// проверяет VO (`Of`), а валидатор формат/длину не повторяет. Находка уровня сервиса, мягкая (Info, «свод», adr/0008).
// Соответствие поле ↔ VO — по имени: VO называется как поле или оканчивается на него (Title ↔ TagTitle), либо поле оканчивается
// на имя VO (CustomerInn ↔ Inn). Чистая функция над IValidationSource.
public static class DoubleValidationDetector
{
    public const string Kind = "double-validation";
    public const int MaxItems = 1000;
    private const int MinReverseNameLength = 3;     // поле оканчивается на имя VO: короткие имена (Id) дали бы шум

    public static IReadOnlyList<SystemProblem> Detect(IReadOnlyList<ServiceAnalysis> analyses)
    {
        var problems = new List<SystemProblem>();
        foreach (var sa in analyses)
        {
            var vos = sa.Validation.ValueObjects.Where(v => !IsTests(v.Layer)).Select(v => v.Name).Distinct(StringComparer.Ordinal).ToList();
            if (vos.Count == 0) continue;
            var index = new VoIndex(vos);                       // один раз на сервис
            var items = new List<ProblemItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in sa.Validation.Rules.Where(r => !IsTests(r.Layer)).OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line))
            {
                if (r.Checks.Count == 0 || MatchingVo(r.Member, index) is not { } vo) continue;
                if (!seen.Add(r.Validator + "|" + r.Member)) continue;
                if (items.Count >= MaxItems) break;
                items.Add(new ProblemItem($"{r.Validator}.{r.Member} ↔ {vo}", r.Layer,
                    $"валидатор проверяет формат ({string.Join(", ", r.Checks)}), а в сервисе есть {vo} с фабрикой: если это одно правило — оставьте владельцем VO", r.File, r.Line));
            }
            if (items.Count == 0) continue;
            problems.Add(new SystemProblem(ProblemSeverity.Info, Kind,
                $"«{sa.Service.Name}»: {items.Count} полей, возможно, проверяются и валидатором, и Value Object",
                sa.NodeId ?? sa.Location, ProblemCategory.Convention, items));
        }
        return problems;
    }

    // Точное совпадение, затем «VO оканчивается на поле», затем «поле оканчивается на VO». Детерминированно (сортировка по имени).
    // Индекс имён VO, строится один раз на сервис: точные имена и все «хвосты» имён, начинающиеся с заглавной буквы (TagTitle → TagTitle, Title).
    // Так сопоставление не зависит от числа VO: на поле уходит O(длина имени), а не O(число VO).
    private sealed class VoIndex
    {
        public readonly SortedSet<string> Names = new(StringComparer.Ordinal);
        public readonly Dictionary<string, SortedSet<string>> BySuffix = new(StringComparer.Ordinal);

        public VoIndex(IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                Names.Add(n);
                for (var i = 1; i < n.Length; i++)
                    if (char.IsUpper(n[i])) Add(n[i..], n);
            }
        }

        private void Add(string suffix, string vo)
        {
            if (!BySuffix.TryGetValue(suffix, out var set)) BySuffix[suffix] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(vo);
        }
    }

    // Поле — последний сегмент пути (Customer.Name → Name). Подходящих VO в уровне может быть несколько: не угадываем, называем всех.
    // Уровни: точное имя; «VO оканчивается на поле» (по границе слова, поле от трёх букв: Id/No дали бы шум); «поле оканчивается на VO»
    // (VO от трёх букв, по границе слова).
    private static string? MatchingVo(string memberPath, VoIndex index)
    {
        var member = memberPath[(memberPath.LastIndexOf('.') + 1)..];
        if (index.Names.Contains(member)) return member;
        if (member.Length >= MinReverseNameLength && index.BySuffix.TryGetValue(member, out var ending))
            return string.Join(" / ", ending.Where(v => v.Length > member.Length).Take(3));
        for (var i = 1; i < member.Length; i++)
            if (char.IsUpper(member[i]) && member.Length - i >= MinReverseNameLength && index.Names.Contains(member[i..]))
                return member[i..];
        return null;
    }

    private static bool IsTests(string layer) => layer.Equals("tests", StringComparison.OrdinalIgnoreCase);
}
