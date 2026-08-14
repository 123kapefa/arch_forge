using ArchForge.Analysis.Infrastructure.Roslyn;
using Xunit;

namespace ArchForge.Analysis.Tests;

public class RoslynSemanticTests
{
    private static string Target =>
        Environment.GetEnvironmentVariable("ARCHFORGE_AUTH_SERVICE")
        ?? "/Users/123kapefa/Documents/projects/microservices/auth_service";

    [SkippableFact]
    public void Semantic_resolves_inheritance_chain_beyond_syntactic()
    {
        Skip.IfNot(Directory.Exists(Target), $"нет собранного таргета auth_service ({Target})");

        var semantic = new RoslynSemanticTypeSource(new RoslynTypeSource()).Read(Target);
        var syntactic = new RoslynTypeSource().Read(Target);

        // AuthDbContext : IdentityDbContext<...> — семантика тянет цепочку до DbContext (EF Core),
        // чего синтаксис (только непосредственная база) не видит.
        var db = semantic.FirstOrDefault(t => t.Name == "AuthDbContext");
        Assert.NotNull(db);
        Assert.Contains("DbContext", db!.Bases);

        // Семантика не теряет базы относительно синтаксиса (⊇).
        var synDb = syntactic.First(t => t.Name == "AuthDbContext");
        Assert.All(synDb.Bases, b => Assert.Contains(b, db.Bases));

        // В сумме транзитивных баз строго больше — резолв цепочек реально сработал.
        int semTotal = semantic.Sum(t => t.Bases.Count);
        int synTotal = syntactic.Sum(t => t.Bases.Count);
        Assert.True(semTotal > synTotal, $"semantic {semTotal} ≤ syntactic {synTotal}: цепочки не резолвятся");
    }

    [SkippableFact]
    public void Semantic_falls_back_to_floor_when_no_references()
    {
        // Каталог только с исходником, без bin/ и без ссылок → семантика бессмысленна → пол.
        string tmp = Path.Combine(Path.GetTempPath(), "archforge_sem_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Foo.cs"), "namespace X; public class Foo { }");
            var floor = new RoslynTypeSource();
            var semantic = new RoslynSemanticTypeSource(floor).Read(tmp);
            // Должны получить тот же результат, что и пол (не пусто, не падение).
            Assert.Contains(semantic, t => t.Name == "Foo");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
