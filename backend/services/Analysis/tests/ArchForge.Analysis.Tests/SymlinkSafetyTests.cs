using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Manifests;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Разбор не должен следовать по симлинкам (#158): симлинк внутри проекта на папку или файл вне него читал бы чужой код/конфигурацию,
// а петля симлинков раскручивалась бы многократно. Дерево: proj/ — проект; outside/ — чужая папка; в proj лежат ссылки на outside и на сам proj.
public class SymlinkSafetyTests : IDisposable
{
    private readonly string _base = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_sym_" + Guid.NewGuid().ToString("N"))).FullName;
    private string Proj => Path.Combine(_base, "proj");
    private string Outside => Path.Combine(_base, "outside");
    private readonly bool _links;

    public SymlinkSafetyTests()
    {
        Write("proj/src/Svc.Domain/A.cs", "namespace P; public class Inside { }");
        Write("proj/Svc.slnx", "<Solution/>");
        Write("proj/docker-compose.yml", "services:\n  svc:\n    image: app-svc\n");
        Write("proj/svc/appsettings.json", "{ \"Own\": { \"Url\": \"http://svc-peer:8/\" } }");
        Write("proj/.hid/Hidden.cs", "namespace H; public class Hidden { }");
        Write("outside/Secret.cs", "namespace O; public class Outside { }");
        Write("outside/Evil.slnx", "<Solution/>");
        Write("outside/docker-compose.yml", "services:\n  stolen:\n    image: app-stolen\n");
        Write("outside/appsettings.json", "{ \"Foreign\": { \"Url\": \"http://stolen:9/\" } }");
        Write("outside/Evil.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Stolen.Package\" Version=\"1.0.0\" /></ItemGroup></Project>");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(Proj, "link"), Outside);                 // папка наружу
            Directory.CreateSymbolicLink(Path.Combine(Proj, "src", "loop"), Proj);              // петля на корень проекта
            File.CreateSymbolicLink(Path.Combine(Proj, "src", "Svc.Domain", "Ext.cs"), Path.Combine(Outside, "Secret.cs"));   // файл наружу
            _links = true;
        }
        catch when (OperatingSystem.IsWindows()) { _links = false; }                            // Windows без права на симлинки — тесты пропускаются
        // на Linux/macOS ссылки создаются всегда: сбой — ошибка среды, а не повод тихо зеленеть
    }

    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch { /* временная папка */ } }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(_base, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public void Safe_enumeration_skips_linked_directories_files_and_loops_but_finds_real_files()
    {
        if (!_links) return;
        var cs = SafeFiles.Enumerate(Proj, "*.cs", skipHidden: true).Select(Path.GetFileName).ToList();
        Assert.Equal(["A.cs"], cs);                                  // ни Secret.cs через link/, ни Ext.cs (файл-ссылка), ни повторов через loop/
        Assert.Single(SafeFiles.Enumerate(Proj, "*.slnx"));
        Assert.Single(SafeFiles.Enumerate(Proj, "*compose*"));
    }

    [Fact]
    public void Roslyn_scan_does_not_read_code_through_symlinks()
    {
        if (!_links) return;
        var files = RoslynScan.EnumerateCs(Proj).Select(Path.GetFileName).ToList();
        Assert.Equal(["A.cs"], files);
        var types = new RoslynTypeSource().Read(Proj);
        Assert.DoesNotContain(types, t => t.Name == "Outside");
        Assert.Contains(types, t => t.Name == "Inside");
    }

    [Fact]
    public void Discovery_and_system_source_ignore_linked_solutions_compose_and_settings()
    {
        if (!_links) return;
        var services = new FolderDiscoverySource().Discover(Proj);
        Assert.DoesNotContain(services, s => s.Name == "Evil");
        var g = new ComposeSystemSource().Read(Proj);
        Assert.DoesNotContain(g.Nodes, n => n.Name == "stolen");                            // compose из link/ не подхвачен
        Assert.DoesNotContain(g.ConfigUrls ?? [], c => c.Url.Contains("stolen"));            // appsettings из link/ не прочитан
    }

    [Fact]
    public void Imports_do_not_follow_linked_projects()
    {
        if (!_links) return;
        var refs = new CsprojImportSource().Read(Proj);
        Assert.DoesNotContain(refs, r => r.Name == "Stolen.Package");
    }

    [Fact]
    public void Root_given_as_a_symlink_still_works_for_the_user()
    {
        if (!_links) return;
        var viaLink = Path.Combine(_base, "rootlink");
        Directory.CreateSymbolicLink(viaLink, Proj);                                          // пользователь сам открыл проект через ссылку
        Assert.Equal(["A.cs"], SafeFiles.Enumerate(viaLink, "*.cs", skipHidden: true).Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void Hidden_directories_are_skipped_only_where_they_always_were()
    {
        if (!_links) return;
        // как прежний EnumerationOptions по умолчанию: discovery/compose/appsettings не заходят в скрытое…
        Assert.DoesNotContain(SafeFiles.Enumerate(Proj, "*.cs", skipHidden: true), f => f.Contains(".hid"));
        // …а обход кода скрытое видел и видит (PathFilter отсекает позже, по своей политике)
        Assert.Contains(SafeFiles.Enumerate(Proj, "*.cs"), f => f.EndsWith("Hidden.cs"));
    }

    [Fact]
    public void Deep_trees_within_the_limit_are_found_and_pattern_case_follows_the_platform()
    {
        var dir = Proj;
        for (var i = 0; i < 70; i++) dir = Path.Combine(dir, "d");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Deep.cs"), "class D { }");
        Assert.Contains(SafeFiles.Enumerate(Proj, "*.cs"), f => f.EndsWith("Deep.cs"));      // 70 уровней < MaxDepth
        Write("proj/Upper.CS", "class U { }");
        var found = SafeFiles.Enumerate(Proj, "*.cs").Any(f => f.EndsWith("Upper.CS"));
        Assert.Equal(!OperatingSystem.IsLinux(), found);                                      // регистр маски — как у ОС
    }

    [Fact]
    public void Discovery_and_system_source_still_find_the_real_project_data()
    {
        if (!_links) return;
        Assert.Contains(new FolderDiscoverySource().Discover(Proj), s => s.Name == "Svc");
        var g = new ComposeSystemSource().Read(Proj);
        Assert.Contains(g.Nodes, n => n.Name == "svc");
        Assert.NotNull(g.ConfigUrls);                                                          // не пусто из-за ошибки: свой appsettings прочитан
    }

    [Fact]
    public void Overlay_symlink_file_is_not_picked_up()
    {
        if (!_links) return;
        Write("outside/docker-compose.dev.yml", "services:\n  svc:\n    image: stolen-image\n");
        File.CreateSymbolicLink(Path.Combine(Proj, "docker-compose.dev.yml"), Path.Combine(Outside, "docker-compose.dev.yml"));
        var g = new ComposeSystemSource().Read(Proj, "dev");
        Assert.DoesNotContain(g.Nodes, n => n.Image == "stolen-image");
    }

    // ── прямые чтения по путям из содержимого недоверенного проекта ──

    [Fact]
    public void Compose_env_file_and_dot_env_cannot_leave_the_root_by_path_or_symlink()
    {
        if (!_links) return;
        Write("outside/secret.env", "SECRET_URL=http://secret-host/\n");
        Write("outside/dot.env", "DOTVAR=http://dotenv-host/\n");
        Write("envroot/ok.env", "OWN_URL=http://own-peer/\n");
        Write("envroot/docker-compose.yml", "services:\n  svc:\n    image: app-svc\n    environment:\n      DOT_URL: ${DOTVAR}\n    env_file:\n"
            + $"      - {Path.Combine(Outside, "secret.env")}\n      - ../outside/secret.env\n      - link/secret.env\n      - ok.env\n");
        Directory.CreateSymbolicLink(Path.Combine(_base, "envroot", "link"), Outside);
        File.CreateSymbolicLink(Path.Combine(_base, "envroot", ".env"), Path.Combine(Outside, "dot.env"));

        var urls = new ComposeSystemSource().Read(Path.Combine(_base, "envroot")).ConfigUrls ?? [];
        Assert.DoesNotContain(urls, c => c.Url.Contains("secret-host"));                       // абсолютный путь, `..` и симлинк-папка
        Assert.DoesNotContain(urls, c => c.Url.Contains("dotenv-host"));                       // .env — симлинк наружу
        Assert.Contains(urls, c => c.Url.Contains("own-peer"));                                 // обычный env_file внутри корня читается
    }

    [Fact]
    public void Project_reference_through_a_symlinked_directory_is_not_a_library()
    {
        if (!_links) return;
        Write("outside/lib/Pay.Contracts/Pay.Contracts.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("outside/lib/Pay.Contracts/Ext.cs", "public static class E { public static IServiceCollection AddPay(this IServiceCollection s) { s.AddHttpClient<A>(); return s; } }");
        Write("sysroot/consumer/Consumer.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\shared\\Pay.Contracts\\Pay.Contracts.csproj\" /></ItemGroup></Project>");
        Write("sysroot/consumer/Reg.cs", "class R { void M(IServiceCollection s) { s.AddPay(); } }");
        Directory.CreateSymbolicLink(Path.Combine(_base, "sysroot", "shared"), Path.Combine(Outside, "lib"));

        Assert.Empty(new CsprojClientLibraryResolver().Resolve(Path.Combine(_base, "sysroot", "consumer"), Path.Combine(_base, "sysroot")));
    }

    [Fact]
    public void Central_package_versions_file_that_is_a_symlink_is_not_read()
    {
        if (!_links) return;
        Write("outside/Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"OutsidePkg\" Version=\"6.6.6\" /></ItemGroup></Project>");
        Write("imp/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"OutsidePkg\" /></ItemGroup></Project>");
        File.CreateSymbolicLink(Path.Combine(_base, "imp", "Directory.Packages.props"), Path.Combine(Outside, "Directory.Packages.props"));

        var refs = new CsprojImportSource().Read(Path.Combine(_base, "imp"));
        Assert.DoesNotContain(refs, r => r.Version == "6.6.6");
    }

    [Fact]
    public void Conventions_file_that_is_a_symlink_is_ignored_but_a_regular_one_works()
    {
        if (!_links) return;
        Write("outside/archforge.conventions.json", "{ \"markers\": { \"IEvil\": \"Evil\" } }");
        Write("conv/linked/x.txt", "x");
        File.CreateSymbolicLink(Path.Combine(_base, "conv", "linked", "archforge.conventions.json"), Path.Combine(Outside, "archforge.conventions.json"));
        Assert.False(ConventionPack.Load(Path.Combine(_base, "conv", "linked")).Markers.ContainsKey("IEvil"));

        Write("conv/real/archforge.conventions.json", "{ \"markers\": { \"IGood\": \"Good\" } }");
        Assert.True(ConventionPack.Load(Path.Combine(_base, "conv", "real")).Markers.ContainsKey("IGood"));       // контроль: обычный файл читается
    }
}
