using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

public class RoslynScanTests
{
    [Fact]
    public void Skips_hidden_and_build_dirs_but_keeps_dotted_project_folders()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "af_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "src"));
        Directory.CreateDirectory(Path.Combine(tmp, ".claude", "worktrees", "wt", "src")); // git-worktree копия
        Directory.CreateDirectory(Path.Combine(tmp, "Auth.API"));                            // точка в имени — НЕ скрытая
        Directory.CreateDirectory(Path.Combine(tmp, "obj"));
        try
        {
            File.WriteAllText(Path.Combine(tmp, "src", "Foo.cs"), "class Foo{}");
            File.WriteAllText(Path.Combine(tmp, "Auth.API", "Bar.cs"), "class Bar{}");
            File.WriteAllText(Path.Combine(tmp, ".claude", "worktrees", "wt", "src", "Foo.cs"), "class Foo{}"); // копия
            File.WriteAllText(Path.Combine(tmp, "obj", "Gen.cs"), "class Gen{}");

            var files = RoslynScan.EnumerateCs(tmp).Select(Path.GetFileName).OrderBy(x => x).ToList();

            // копия из .claude/ и генерённое из obj/ отсеяны; папка с точкой (Auth.API) сохранена → без дублей
            Assert.Equal(new[] { "Bar.cs", "Foo.cs" }, files);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // #167: враждебный файл с глубокой вложенностью не должен дойти до ParseText (StackOverflow не ловится catch).
    [Theory]
    [InlineData("angle")]
    [InlineData("paren")]
    [InlineData("brace")]
    [InlineData("interp")]
    public void Skips_deeply_nested_files_and_keeps_neighbours(string kind)
    {
        const int n = 100_000;
        var hostile = kind switch
        {
            "angle" => "class A { " + string.Concat(Enumerable.Repeat("Task<", n)) + "int" + new string('>', n) + " f; }",
            "paren" => "class A { int f = " + new string('(', n) + "1" + new string(')', n) + "; }",
            "brace" => "class A { void M() " + string.Concat(Enumerable.Repeat("{", n)) + string.Concat(Enumerable.Repeat("}", n)) + " }",
            _ => "class A { string s = " + string.Concat(Enumerable.Repeat("$\"{", n)) + "1" + string.Concat(Enumerable.Repeat("}\"", n)) + "; }",
        };
        var tmp = Path.Combine(Path.GetTempPath(), "af_deep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Hostile.cs"), hostile);
            File.WriteAllText(Path.Combine(tmp, "Good.cs"), "class Good { bool M(int a, int b) => a < b && b > a; }");
            var files = RoslynScan.EnumerateCs(tmp).Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "Good.cs" }, files);
            // и полный разбор источником типов не падает процессом
            Assert.Contains(new RoslynTypeSource().Read(tmp), t => t.Name == "Good");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Ordinary_nested_code_with_comparisons_is_not_skipped()
    {
        var code = "class C { void M(int a, int b) { if (a < b) { for (var i = 0; i < 9; i++) { var d = new Dictionary<string, List<Dictionary<int, Task<int>>>>(); } } } }\n"
                 + string.Concat(Enumerable.Repeat("bool f(int a,int b)=>a<b;\n", 2000));
        Assert.False(RoslynScan.ExceedsDepth(code));
    }
}
