using ArchForge.Analysis.Infrastructure.Roslyn;
using Xunit;

namespace ArchForge.Analysis.Tests;

// #167: StackOverflow в ParseText не ловится try/catch и роняет весь процесс Analysis. Эти тесты сами и есть проверка:
// если предфильтр сломан, упадёт тестовый хост целиком (а не одна проверка).
public class RoslynDeepNestingTests
{
    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    // Размер далеко за границей падения (на 1,5 МБ стека): угловые ~1000+, круглые ~4000+, интерполяция ~1000+.
    private const int Hostile = 100_000;

    public static TheoryData<string, string, bool> HostileFiles => new()
    {
        { "angle",   "using System.Threading.Tasks; class Hostile { " + Rep("Task<", Hostile) + "int" + Rep(">", Hostile) + " x; }" , true },
        { "paren",   "class Hostile { int x = " + Rep("(", Hostile) + "1" + Rep(")", Hostile) + "; }" , true },
        { "paren-open-only", "class Hostile { int x = " + Rep("(", Hostile) , true },
        { "interp",  "class Hostile { var x = " + Rep("$\"{", Hostile) + "1" + Rep("}\"", Hostile) + "; }" , true },
        { "brace",   "class Hostile { void M() " + Rep("{", Hostile) + Rep("}", Hostile) + " }" , true },
        { "square",  "class Hostile { int x = " + Rep("a[", Hostile) + "1" + Rep("]", Hostile) + "; }" , true },
        // Обходы текстового счётчика (#167, security-ревью): скобки спрятаны в комментариях, глубина без скобок. Ловит их поток с большим стеком.
        { "paren-in-comment", "class Hostile { int x = " + Rep("(/*)*/", 10_000) + "1" + Rep(")", 10_000) + "; }" , false },
        { "angle-in-comment", "class Hostile { " + Rep("Task</*>*/", 20_000) + "int" + Rep(">", 20_000) + " x; }" , false },
        { "angle-preproc",    "class Hostile { " + Rep("Task<\n#if X\n;\n#endif\n", 3_000) + "int" + Rep(">", 3_000) + " x; }" , false },
        { "not-pattern",     "class Hostile { bool x = o is " + Rep("not ", 200_000) + "null; }" , false },
        { "doc-xml",         "class Hostile {\n/// " + Rep("<a>", 100_000) + Rep("</a>", 100_000) + "\nvoid M(){} }" , false },
        { "cond-access",     "class Hostile { object x = a" + Rep("?.b", 200_000) + "; }" , false },
        { "qualified-base",  "class Hostile : A" + Rep(".B", 100_000) + " {}" , false },
        { "qualified-ns",    "namespace A" + Rep(".B", 200_000) + " { class Hostile {} }" , false },
        { "throw-cond",      "class Hostile { void M(){ throw e" + Rep("?.b", 20_000) + "; } }" , false },
    };

    [Theory]
    [MemberData(nameof(HostileFiles))]
    public void Hostile_file_does_not_crash_and_neighbours_still_parsed(string kind, string hostile, bool skippedByFilter)
    {
        // Ключевые слова, по которым источники вообще решают парсить файл (быстрый отсев по тексту), — иначе тест ничего не проверит.
        var bait = "\n// ArgumentNullException Result Exception AddHttpClient .Map RestService\n";
        var tmp = MakeTemp();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Good.cs"), "namespace X; public class Good { }");
            File.WriteAllText(Path.Combine(tmp, "Hostile.cs"), hostile + bait);

            var types = new RoslynTypeSource().Read(tmp);                 // не должно уронить процесс (StackOverflow)
            _ = new RoslynSemanticTypeSource(new RoslynTypeSource()).Read(tmp);
            _ = new RoslynConstructionSource().Read(tmp);
            _ = new RoslynMinimalApiSource().Read(tmp);
            _ = new RoslynHttpCallSource().Read(tmp);
            _ = new RoslynGuardSource().Read(tmp);
            _ = new RoslynErrorStrategySource().Read(tmp);

            Assert.Contains(types, t => t.Name == "Good");
            if (skippedByFilter) Assert.DoesNotContain(types, t => t.Name == "Hostile");   // скобочный предфильтр пропускает файл целиком; остальные держит стек
        Assert.False(string.IsNullOrEmpty(kind));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Nesting_depth_boundary_is_inclusive_at_the_limit()
    {
        var room = RoslynScan.MaxNestingDepth - 1;                        // «{» обёртки класса уже занимает один уровень
        var atLimit = Rep("(", room) + Rep(")", room);
        var over = Rep("(", room + 1) + Rep(")", room + 1);

        Assert.NotNull(RoslynScan.TryParse("class A { int x = " + atLimit + "; }", "a.cs"));
        Assert.Null(RoslynScan.TryParse("class A { int x = " + over + "; }", "a.cs"));
    }

    [Fact]
    public void Realistic_code_is_not_flagged()
    {
        // Много сравнений «<» в одном файле и одном выражении — не копят глубину (счётчик «<» сбрасывается на «; { }»).
        var compares = Rep("if (a < b && c < d) { x = a < b ? 1 : 2; }\n", 2000);
        var forLoops = Rep("for (var i = 0; i < n; i++) { for (var j = 0; j < m; j++) { s += i < j ? 1 : 0; } }\n", 1000);
        var generics = "Dictionary<string, List<Dictionary<int, Task<IReadOnlyList<KeyValuePair<string, Func<int, Task<bool>>>>>>>> d;";
        var nested = Rep("if (a) {", 40) + Rep("}", 40);                                  // глубже реального кода с запасом
        var text = "class A { void M() {\n" + compares + forLoops + generics + "\n" + nested + "\n} }";

        Assert.True(RoslynScan.NestingDepth(text) < RoslynScan.MaxNestingDepth / 2);
        Assert.NotNull(RoslynScan.TryParse(text, "a.cs"));
    }

    [Fact]
    public void Unbalanced_brackets_in_full_line_comments_do_not_accumulate()
    {
        var comments = Rep("    // смайл :( и «(см. выше» и { и <\n", 500) + Rep("/// <summary>1) пункт ( [\n", 500);
        var text = "class A {\n" + comments + "}";
        Assert.True(RoslynScan.NestingDepth(text) <= 1);
    }

    // Охрана от забытого места: любой новый источник обязан ходить через RoslynScan.TryParse*, а не звать ParseText сам.
    [Fact]
    public void Only_RoslynScan_calls_ParseText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ArchForge.Analysis.Infrastructure.Roslyn"))) dir = dir.Parent;
        Assert.NotNull(dir);   // тест лежит в services/Analysis/tests/…, искомый каталог на уровень выше «tests»

        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "ArchForge.Analysis.Infrastructure.Roslyn"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "RoslynScan.cs" && File.ReadAllText(f).Contains("ParseText(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string MakeTemp()
    {
        var p = Path.Combine(Path.GetTempPath(), "archforge_deep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Cleanup(string p)
    {
        try { Directory.Delete(p, recursive: true); } catch { /* best-effort */ }
    }
}
