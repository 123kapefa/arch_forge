using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

public class RoslynConstructionSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "af-ctor-" + Guid.NewGuid().ToString("N"));

    private void Write(string rel, string code)
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, code);
    }

    [Fact]
    public void Records_new_of_own_types_with_context_ignores_framework()
    {
        Write("Widget.Contracts/WidgetDto.cs", """
            namespace W;
            public record WidgetDto(string Name);
            """);
        Write("Widget.Api/GetWidgetsQueryHandler.cs", """
            namespace W;
            using System.Collections.Generic;
            public class GetWidgetsQueryHandler
            {
                public object Handle()
                {
                    var xs = new List<int>();      // фреймворк-тип — не должен попасть в ось
                    return new WidgetDto("a");
                }
            }
            """);
        Write("Widget.Api/WidgetMapper.cs", """
            namespace W;
            public static class WidgetMapper
            {
                public static object ToDto() => new WidgetDto("b");
            }
            """);

        var sites = new RoslynConstructionSource().Read(_dir);

        var widget = sites.Where(s => s.TypeName == "WidgetDto").ToList();
        Assert.Equal(2, widget.Count);                                    // оба new WidgetDto найдены
        Assert.DoesNotContain(sites, s => s.TypeName == "List");          // фреймворк-тип не записан (не свой)
        Assert.Contains(widget, s => s is { InType: "GetWidgetsQueryHandler", InMethod: "Handle", Layer: "Api" });
        Assert.Contains(widget, s => s.InType == "WidgetMapper");         // сайт внутри маппера тоже фиксируем (детектор отфильтрует)
        Assert.All(widget, s => Assert.True(s.Line > 0));                 // строка проставлена (для перехода к месту)
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }
}
