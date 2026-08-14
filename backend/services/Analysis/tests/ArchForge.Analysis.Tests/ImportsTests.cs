using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Roslyn;
using Xunit;

namespace ArchForge.Analysis.Tests;

public class ImportsTests
{
    [Fact]
    public void Aggregates_versions_usedby_and_conflict()
    {
        var refs = new[]
        {
            new PackageRef("EF", "8.0.0", "Api"),
            new PackageRef("EF", "9.0.0", "Infra"),   // конфликт версий между проектами
            new PackageRef("Serilog", "3.0.0", "Api"),
            new PackageRef("Serilog", "3.0.0", "Infra"),
        };

        var dto = ImportMapper.ToDto(refs, "svc");

        var ef = dto.Packages.First(p => p.Name == "EF");
        Assert.True(ef.VersionConflict);
        Assert.Equal(new[] { "8.0.0", "9.0.0" }, ef.Versions);
        Assert.Equal(new[] { "Api", "Infra" }, ef.UsedBy);

        var serilog = dto.Packages.First(p => p.Name == "Serilog");
        Assert.False(serilog.VersionConflict);

        Assert.Equal("EF", dto.Packages[0].Name); // конфликты — выше в списке
    }

    [Fact]
    public void Skips_conditional_and_update_overrides_include_within_project()
    {
        using var t = new TempDir();
        t.Write("Directory.Packages.props",
            "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"3.0.0\" /></ItemGroup></Project>");
        t.Write("A/A.csproj",
            "<Project><ItemGroup>"
            + "<PackageReference Include=\"Serilog\" />"                                                 // версия из props (3.0.0)
            + "<PackageReference Update=\"Serilog\" Version=\"3.1.0\" />"                                // Update переопределяет → 3.1.0
            + "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" Condition=\"'$(x)'=='y'\" />" // условная — пропуск
            + "</ItemGroup></Project>");

        var refs = new CsprojImportSource().Read(t.Path);

        var serilog = refs.Where(r => r.Name == "Serilog").ToList();
        Assert.Single(serilog);                    // одна ссылка на проект (не дубль Include+Update)
        Assert.Equal("3.1.0", serilog[0].Version); // Update победил
        Assert.DoesNotContain(refs, r => r.Name == "Newtonsoft.Json"); // условная пропущена
    }

    [Fact]
    public void Nearest_directory_packages_props_wins_per_project()
    {
        using var t = new TempDir();
        t.Write("Directory.Packages.props",
            "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"1.0.0\" /></ItemGroup></Project>");
        t.Write("B/Directory.Packages.props",
            "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"9.0.0\" /></ItemGroup></Project>");
        t.Write("A/A.csproj", "<Project><ItemGroup><PackageReference Include=\"Serilog\" /></ItemGroup></Project>");
        t.Write("B/B.csproj", "<Project><ItemGroup><PackageReference Include=\"Serilog\" /></ItemGroup></Project>");

        var refs = new CsprojImportSource().Read(t.Path);

        // A видит корневой props (1.0.0), B — свой ближайший (9.0.0): реальный кросс-сервисный конфликт виден.
        Assert.Equal("1.0.0", refs.First(r => r.Project == "A").Version);
        Assert.Equal("9.0.0", refs.First(r => r.Project == "B").Version);
        Assert.True(ImportMapper.ToDto(refs, "svc").Packages.First(p => p.Name == "Serilog").VersionConflict);
    }

    [SkippableFact]
    public void Reads_packages_from_auth_service_csproj()
    {
        string target = Environment.GetEnvironmentVariable("ARCHFORGE_AUTH_SERVICE")
            ?? "/Users/123kapefa/Documents/projects/microservices/auth_service";
        Skip.IfNot(Directory.Exists(target), $"нет таргета ({target})");

        var refs = new CsprojImportSource().Read(target);
        Assert.NotEmpty(refs);
        Assert.Contains(refs, r =>
            r.Name.Contains("EntityFrameworkCore") || r.Name.Contains("OpenIddict") || r.Name.Contains("Identity"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "archforge_imp_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Write(string relative, string content)
        {
            string full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
