using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Синтаксический детектор minimal-API эндпоинтов (app.MapGet/… с литеральным маршрутом) — #71.
public class RoslynMinimalApiSourceTests
{
    [Fact]
    public void Detects_map_verbs_with_literal_route()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "af_min_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Program.cs"), """
                var app = WebApplication.Create();
                app.MapGet("/api/auth/csrf", () => "x");
                app.MapPost("/api/auth/login", () => "y");
                app.MapControllers();              // не Map-verb → игнор
                app.MapGet(SomeConst, () => "z");   // маршрут не литерал → пропуск (без семантики не резолвим)
                app.Run();
                """);
            var hits = new RoslynMinimalApiSource().Read(tmp);

            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, h => h.Method == "GET" && h.Route == "/api/auth/csrf");
            Assert.Contains(hits, h => h.Method == "POST" && h.Route == "/api/auth/login");
            Assert.All(hits, h => Assert.EndsWith("Program.cs", h.File));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Empty_when_no_minimal_api()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "af_min_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Foo.cs"), "public class Foo { }");
            Assert.Empty(new RoslynMinimalApiSource().Read(tmp));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
