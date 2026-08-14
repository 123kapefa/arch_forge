using ArchForge.Agent.Domain;
using ArchForge.Agent.Infrastructure;

namespace ArchForge.Agent.Tests;

public class ClaudeCliRunnerTests
{
    [Fact]
    public void BuildArguments_keeps_prompt_as_single_argument()
    {
        var runner = new ClaudeCliRunner(new GitWorkspace());
        var args = runner.BuildArguments("удали файл; rm -rf / && echo $(whoami)");

        Assert.Equal("-p", args[0]);
        Assert.Equal("удали файл; rm -rf / && echo $(whoami)", args[1]); // метасимволы — часть ОДНОГО аргумента
        Assert.Equal(2, args.Count);
    }

    [Fact]
    public void BuildArguments_appends_configured_extra_args()
    {
        var runner = new ClaudeCliRunner(new GitWorkspace(), new ClaudeCliOptions(ExtraArgs: ["--some-flag"]));
        var args = runner.BuildArguments("hi");

        Assert.Equal(new[] { "-p", "hi", "--some-flag" }, args);
    }

    [SkippableFact]
    public async Task Rejects_non_git_directory()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        string dir = Path.Combine(Path.GetTempPath(), "archforge_nogit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var runner = new ClaudeCliRunner(new GitWorkspace());
            var result = await runner.RunAsync(new AgentRunRequest(dir, "правь", TimeSpan.FromSeconds(30)));

            Assert.Equal(AgentStatus.Failed, result.Status);
            Assert.Contains("git", result.Error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [SkippableFact]
    public async Task Rejects_dirty_working_tree()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        using var repo = await GitTestRepo.InitAsync();
        repo.Write("a.txt", "v1\n");
        await repo.CommitAllAsync("init");
        repo.Write("a.txt", "dirty\n"); // незакоммиченная правка → прогон должен быть отклонён

        var runner = new ClaudeCliRunner(new GitWorkspace());
        var result = await runner.RunAsync(new AgentRunRequest(repo.Dir, "правь", TimeSpan.FromSeconds(30)));

        Assert.Equal(AgentStatus.Failed, result.Status);
        Assert.Contains("чист", result.Error); // «рабочее дерево не чистое»
    }

    // Реальный headless-прогон Claude — двойной gate: опт-ин через ARCHFORGE_RUN_CLAUDE_IT=1 И наличие бинаря.
    // По умолчанию пропускается (обычный dotnet test не тратит подписку и не ждёт минуты).
    [SkippableFact]
    public async Task Runs_claude_headless_and_captures_diff()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ARCHFORGE_RUN_CLAUDE_IT") == "1",
            "интеграция с claude включается ARCHFORGE_RUN_CLAUDE_IT=1");
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        Skip.IfNot(ClaudeAvailable(), "claude не найден в PATH");

        using var repo = await GitTestRepo.InitAsync();
        repo.Write("hello.txt", "hello\n");
        await repo.CommitAllAsync("init");

        var runner = new ClaudeCliRunner(new GitWorkspace(),
            new ClaudeCliOptions(ExtraArgs: ["--dangerously-skip-permissions"]));
        var result = await runner.RunAsync(new AgentRunRequest(
            repo.Dir,
            "Замени содержимое файла hello.txt на слово goodbye и больше ничего не делай.",
            TimeSpan.FromMinutes(3)));

        // ИИ жёстко не проверяем; важно, что петля отработала: финальный статус + diff, если правил.
        Assert.True(result.Status is AgentStatus.Succeeded or AgentStatus.Failed);
        if (result.ChangedFiles.Count > 0) Assert.False(string.IsNullOrEmpty(result.Diff));
    }

    private static bool ClaudeAvailable()
    {
        try
        {
            var r = ProcessRunner.RunAsync("claude", ["--version"], Path.GetTempPath(), TimeSpan.FromSeconds(15))
                .GetAwaiter().GetResult();
            return r.ExitCode == 0;
        }
        catch { return false; }
    }
}
