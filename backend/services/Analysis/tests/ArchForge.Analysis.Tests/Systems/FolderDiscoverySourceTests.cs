using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

public class FolderDiscoverySourceTests
{
    private const string Workspace = "/Users/123kapefa/Documents/projects/product-site-workspace";

    private static string TempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_disc_" + Guid.NewGuid().ToString("N"))).FullName;

    private static void MakeService(string root, string dir, string sln, string composeName)
    {
        var p = Directory.CreateDirectory(Path.Combine(root, dir)).FullName;
        File.WriteAllText(Path.Combine(p, sln), "<Solution/>");
        File.WriteAllText(Path.Combine(p, composeName), "services:\n  api: { image: app:1 }\n");
    }

    [Fact]
    public void Discovers_services_by_slnx_and_pairs_compose_ignoring_noise()
    {
        var root = TempRoot();
        MakeService(root, "svc-a", "A.slnx", "docker-compose.yml");
        MakeService(root, "svc-b", "B.sln", "docker-compose-dev.yaml"); // нестандартное имя compose
        // шум: worktree со своим .slnx — НЕ сервис
        var wt = Directory.CreateDirectory(Path.Combine(root, "svc-a", ".claude", "worktrees", "x")).FullName;
        File.WriteAllText(Path.Combine(wt, "A.slnx"), "<Solution/>");
        try
        {
            var found = new FolderDiscoverySource().Discover(root);
            Assert.Equal(2, found.Count); // worktree-копия не считается
            Assert.Contains(found, s => s.Name == "A" && s.ComposePath is { } c && c.EndsWith("docker-compose.yml"));
            Assert.Contains(found, s => s.Name == "B" && s.ComposePath is { } c && c.EndsWith("docker-compose-dev.yaml"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void File_path_returns_empty()
    {
        var f = Path.Combine(TempRoot(), "x.txt");
        File.WriteAllText(f, "not a dir");
        try { Assert.Empty(new FolderDiscoverySource().Discover(f)); }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, recursive: true); }
    }

    // ---- реальный таргет (де-риск P1.5-B; на CI скипается) ----

    [SkippableFact]
    public void Real_workspace_finds_three_dotnet_services()
    {
        Skip.IfNot(Directory.Exists(Workspace), "таргет product-site-workspace не найден");

        var found = new FolderDiscoverySource().Discover(Workspace);

        Assert.Contains(found, s => s.Name == "AuthService");
        Assert.Contains(found, s => s.Name == "LoyaltyService");
        // product_site подхватывается через нестандартный docker-compose-dev.yaml (закрыта находка P1.5-0)
        Assert.Contains(found, s => s.Name == "KalmykovGroup.Server"
            && s.ComposePath is { } c && c.EndsWith("docker-compose-dev.yaml"));
        // бэкапы/worktrees не считаются сервисами
        Assert.DoesNotContain(found, s => s.Root.Contains("backup") || s.Root.Contains("worktrees"));
    }

    [SkippableFact]
    public void Real_single_service_dir_is_one()
    {
        var authDir = Workspace + "/auth-service";
        Skip.IfNot(Directory.Exists(authDir), "таргет не найден");

        var found = new FolderDiscoverySource().Discover(authDir);

        Assert.Single(found);
        Assert.Equal("AuthService", found[0].Name);
    }
}
