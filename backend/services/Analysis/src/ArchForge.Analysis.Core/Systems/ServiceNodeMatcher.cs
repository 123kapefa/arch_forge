using ArchForge.Analysis.Core.Profiles;

namespace ArchForge.Analysis.Core.Systems;

// Монорепо с ОДНИМ корневым compose: сервисы найдены по .sln (своего compose у них нет), а узлы графа — из корневого compose.
// Привязываем сервис к узлу по ИМЕНИ, не гадая (adr/0008):
//  • точное совпадение нормализованных имён (CommentService ↔ comment-service);
//  • иначе стем узла без суффикса -service/-api/… (≥4 букв) как ПРЕФИКС имени .sln (EducationContentService ↔ education-service);
//    подстрока не годится: «OAuthGateway» содержит «auth», но это не auth-service.
// Сателлиты (-migrations, сидеры, крон) в кандидаты не берём. Зонтичный/общий .sln без узла (backend, Shared, шаблоны) не привязывается.
// Два .sln на один узел: побеждает единственное точное совпадение, иначе оба отбрасываются (не гадаем).
public static class ServiceNodeMatcher
{
    public static IReadOnlyList<(DiscoveredService Svc, SystemNode Node)> Match(
        IReadOnlyList<DiscoveredService> services, IReadOnlyList<SystemNode> nodes, ServiceNaming naming)
    {
        var candidates = nodes.Where(n => n.Kind == SystemNodeKind.Service && !naming.IsSatellite(n.Name)).ToList();
        if (candidates.Count == 0) return [];

        var normByNode = candidates.ToDictionary(n => n.Id, n => Norm(n.Name), StringComparer.Ordinal);
        var stemByNode = candidates
            .Select(n => (Node: n, Stem: Norm(StripSuffix(n.Name, naming))))
            .Where(x => x.Stem.Length >= 4 && x.Stem != normByNode[x.Node.Id])   // короткие и совпадающие с полным именем стемы не нужны
            .ToList();

        var bound = new List<(int Index, DiscoveredService Svc, SystemNode Node, bool Exact)>();
        for (var i = 0; i < services.Count; i++)
        {
            var svc = services[i];
            if (naming.IsSatellite(svc.Name)) continue;                      // решения миграторов/сидеров/крона сервисом не считаем
            var norm = Norm(svc.Name);
            if (norm.Length == 0) continue;
            var exact = candidates.Where(n => normByNode[n.Id] == norm).ToList();
            if (exact.Count == 1) { bound.Add((i, svc, exact[0], true)); continue; }
            if (exact.Count > 1) continue;                                   // одноимённых узлов несколько — не гадаем

            // стем — это ЦЕЛЫЕ ведущие слова имени решения: EducationContentService → «education», «educationcontent», …
            // («AuthorizationService» не начинается со слова «auth», «OrderingService» — со слова «order»)
            var prefixes = LeadingWords(svc.Name);
            var stems = stemByNode.Where(x => prefixes.Contains(x.Stem)).ToList();
            if (stems.Count == 0) continue;
            var longest = stems.Max(x => x.Stem.Length);
            var best = stems.Where(x => x.Stem.Length == longest).ToList();
            if (best.Count == 1) bound.Add((i, svc, best[0].Node, false));    // единственный самый длинный стем
        }

        var result = new List<(int Index, DiscoveredService Svc, SystemNode Node)>();
        foreach (var g in bound.GroupBy(b => b.Node.Id))
        {
            var exact = g.Where(b => b.Exact).ToList();
            if (exact.Count == 1) result.Add((exact[0].Index, exact[0].Svc, exact[0].Node));
            else if (exact.Count == 0 && g.Count() == 1) { var only = g.First(); result.Add((only.Index, only.Svc, only.Node)); }
            // иначе несколько претендентов на один узел — отбрасываем всех
        }
        return result.OrderBy(r => r.Index).Select(r => (r.Svc, r.Node)).ToList();   // порядок = порядок discovery (детерминизм)
    }

    // Накопленные ведущие слова имени, нормализованные: «EducationContentService» → {education, educationcontent, educationcontentservice}.
    // Слова — по разделителям и границам регистра (PascalCase, аббревиатуры: «XMLParser» → XML, Parser).
    private static HashSet<string> LeadingWords(string name)
    {
        var words = new List<string>();
        var cur = new System.Text.StringBuilder();
        void Flush() { if (cur.Length > 0) { words.Add(cur.ToString().ToLowerInvariant()); cur.Clear(); } }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            var boundary = cur.Length > 0 && char.IsUpper(c) &&
                (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary) Flush();
            cur.Append(c);
        }
        Flush();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var acc = "";
        foreach (var w in words) { acc += w; prefixes.Add(acc); }
        return prefixes;
    }

    private static string StripSuffix(string name, ServiceNaming naming)
    {
        var b = name.Trim();
        foreach (var s in naming.TypeSuffixes)
            if (b.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return b[..^s.Length];
        return b;
    }

    // только строчные буквы/цифры: «AuthService» ≡ «auth-service» → «authservice»
    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
