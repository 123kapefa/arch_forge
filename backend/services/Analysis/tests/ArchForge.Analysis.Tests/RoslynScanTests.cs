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
}
