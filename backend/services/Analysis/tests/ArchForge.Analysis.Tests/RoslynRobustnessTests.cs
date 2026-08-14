using ArchForge.Analysis.Infrastructure.Roslyn;
using Xunit;

namespace ArchForge.Analysis.Tests;

public class RoslynRobustnessTests
{
    // Честно: Roslyn-парсер error-tolerant по построению — на мусорном тексте он не бросает
    // исключений (мы на это опираемся, adr/0008). Тест фиксирует ДВЕ зависимости, а не «защиту от
    // краха»: (1) битый файл не роняет весь разбор, (2) валидные соседи всё равно извлекаются
    // (изоляция по файлам). Сломается, если кто-то заменит парсер на бросающий или уберёт per-file try/catch.
    [Fact]
    public void Garbage_file_does_not_break_parsing_of_valid_neighbours()
    {
        string tmp = MakeTemp();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Good.cs"), "namespace X; public class Good { }");
            File.WriteAllText(Path.Combine(tmp, "Garbage.cs"), "public class {{{ не валидный C# @#$% ");

            var types = new RoslynTypeSource().Read(tmp);

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

    // Кап ссылок — единственная новая ветка логики семантики; проверяем границы напрямую (P1-H #3).
    [Theory]
    [InlineData(0, false)]     // нет ссылок → семантики нет
    [InlineData(1, true)]
    [InlineData(RoslynSemanticTypeSource.MaxReferences, true)]      // 4000 — ещё берём
    [InlineData(RoslynSemanticTypeSource.MaxReferences + 1, false)] // 4001 — уже пол
    public void Reference_cap_decides_when_semantic_is_usable(int count, bool usable)
    {
        Assert.Equal(usable, RoslynSemanticTypeSource.ReferencesUsable(count));
    }

    [Fact]
    public void Read_stops_on_cancellation()
    {
        string tmp = MakeTemp();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "A.cs"), "public class A { }");
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel(); // уже отменён — разбор не должен молча досчитать граф

            Assert.Throws<OperationCanceledException>(() => new RoslynTypeSource().Read(tmp, cts.Token));
        }
        finally { Cleanup(tmp); }
    }

    // Рискованный путь: у семантики есть catch(Exception)→floor. Отмена НЕ должна в него попасть
    // (порядок catch: OperationCanceledException раньше). Тест упадёт, если кто-то схлопнет catch-блоки.
    [Fact]
    public void Semantic_read_propagates_cancellation_not_swallowed_as_fallback()
    {
        string tmp = MakeTemp();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "A.cs"), "public class A { }");
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            var semantic = new RoslynSemanticTypeSource(new RoslynTypeSource());
            Assert.Throws<OperationCanceledException>(() => semantic.Read(tmp, cts.Token));
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
