using ArchForge.Analysis.Infrastructure.Roslyn;
using Xunit;

namespace ArchForge.Analysis.Tests;

public class RoslynRobustnessTests
{
    [Fact]
    public void Broken_cs_does_not_crash_and_valid_neighbours_still_parsed()
    {
        string tmp = MakeTemp();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Good.cs"), "namespace X; public class Good { }");
            File.WriteAllText(Path.Combine(tmp, "Broken.cs"), "public class {{{ не валидный C# @#$% ");

            var types = new RoslynTypeSource().Read(tmp);

            // Битый сосед не роняет разбор; валидный тип извлечён (изоляция ошибок по файлам, adr/0008).
            Assert.Contains(types, t => t.Name == "Good");
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void Huge_file_is_skipped_small_still_parsed()
    {
        string tmp = MakeTemp();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Small.cs"), "public class Small { }");
            // >2 МБ: валидный тип, но файл пропускается по размеру (генерённое/данные не парсим).
            File.WriteAllText(Path.Combine(tmp, "Huge.cs"), "public class Big { }\n// " + new string('x', 2_100_000));

            var types = new RoslynTypeSource().Read(tmp);

            Assert.Contains(types, t => t.Name == "Small");
            Assert.DoesNotContain(types, t => t.Name == "Big");
        }
        finally { Cleanup(tmp); }
    }

    private static string MakeTemp()
    {
        string p = Path.Combine(Path.GetTempPath(), "archforge_rob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Cleanup(string p)
    {
        try { Directory.Delete(p, recursive: true); } catch { /* best-effort */ }
    }
}
