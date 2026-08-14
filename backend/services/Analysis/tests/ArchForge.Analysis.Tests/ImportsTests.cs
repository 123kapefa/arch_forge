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
            new PackageRef("EF", "9.0.0", "Infra"),   // конфликт версий
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
}
