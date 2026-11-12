using ArchForge.Agent.Infrastructure;

namespace ArchForge.Agent.Tests;

// Одноразовый temp git-репозиторий для тестов git-петли.
internal sealed class GitTestRepo : IDisposable
{
    public string Dir { get; }

    private GitTestRepo(string dir) => Dir = dir;

    public static async Task<GitTestRepo> InitAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "archforge_agent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await Run("init", "-q");
        await Run("config", "user.email", "test@archforge.local");
        await Run("config", "user.name", "archforge-test");
        await Run("config", "commit.gpgsign", "false");
        return new GitTestRepo(dir);

        Task<ProcessResult> Run(params string[] args) =>
            ProcessRunner.RunAsync("git", args, dir, TimeSpan.FromSeconds(30));
    }

    public void Write(string relPath, string content) =>
        File.WriteAllText(Path.Combine(Dir, relPath), content);

    public string Read(string relPath) => File.ReadAllText(Path.Combine(Dir, relPath));

    public async Task CommitAllAsync(string message)
    {
        await ProcessRunner.RunAsync("git", ["add", "-A"], Dir, TimeSpan.FromSeconds(30));
        await ProcessRunner.RunAsync("git", ["commit", "-q", "-m", message], Dir, TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
    }
}
