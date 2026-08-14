using ArchForge.Agent.Infrastructure;

namespace ArchForge.Agent.Tests;

public class GitWorkspaceTests
{
    [SkippableFact]
    public async Task Detects_git_repo_and_clean_tree()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        using var repo = await GitTestRepo.InitAsync();
        repo.Write("a.txt", "hello\n");
        await repo.CommitAllAsync("init");

        var git = new GitWorkspace();
        Assert.True(await git.IsGitRepoAsync(repo.Dir));
        Assert.True(await git.IsCleanAsync(repo.Dir));

        string plain = Path.Combine(Path.GetTempPath(), "archforge_plain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        try { Assert.False(await git.IsGitRepoAsync(plain)); }
        finally { Directory.Delete(plain, recursive: true); }
    }

    [SkippableFact]
    public async Task Diff_and_changed_files_capture_edits_and_new_files()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        using var repo = await GitTestRepo.InitAsync();
        repo.Write("tracked.txt", "v1\n");
        await repo.CommitAllAsync("init");

        // «агент» правит tracked-файл и создаёт новый
        repo.Write("tracked.txt", "v2\n");
        repo.Write("new.txt", "brand new\n");

        var git = new GitWorkspace();
        Assert.False(await git.IsCleanAsync(repo.Dir));

        var changed = await git.ChangedFilesAsync(repo.Dir);
        Assert.Contains(changed, c => c.Path == "tracked.txt" && c.ChangeKind == "M");
        Assert.Contains(changed, c => c.Path == "new.txt" && c.ChangeKind == "??");

        var diff = await git.DiffAsync(repo.Dir);
        Assert.Contains("v2", diff);
        Assert.Contains("new.txt", diff); // новый файл виден в diff через intent-to-add
    }

    [SkippableFact]
    public async Task Rollback_restores_baseline_pointwise()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        using var repo = await GitTestRepo.InitAsync();
        repo.Write("tracked.txt", "v1\n");
        await repo.CommitAllAsync("init");

        repo.Write("tracked.txt", "v2\n");
        repo.Write("new.txt", "brand new\n");

        var git = new GitWorkspace();
        var changed = await git.ChangedFilesAsync(repo.Dir);
        await git.RollbackAsync(repo.Dir, changed);

        Assert.Equal("v1\n", repo.Read("tracked.txt"));                 // модификация откатана
        Assert.False(File.Exists(Path.Combine(repo.Dir, "new.txt")));   // untracked удалён
        Assert.True(await git.IsCleanAsync(repo.Dir));                  // дерево снова чистое
    }
}
