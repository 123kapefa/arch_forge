using System.Diagnostics;
using ArchForge.Agent.Infrastructure;

namespace ArchForge.Agent.Tests;

public class ProcessRunnerTests
{
    [SkippableFact]
    public async Task Captures_stdout_and_exit_code()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        var r = await ProcessRunner.RunAsync("git", ["--version"], Path.GetTempPath(), TimeSpan.FromSeconds(30));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("git version", r.StdOut);
        Assert.False(r.TimedOut);
        Assert.False(r.Cancelled);
    }

    [SkippableFact]
    public async Task Cancellation_kills_long_running_process()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        var r = await ProcessRunner.RunAsync("sleep", ["30"], Path.GetTempPath(), TimeSpan.FromSeconds(60), cts.Token);
        sw.Stop();

        Assert.True(r.Cancelled);
        Assert.False(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"процесс не убит вовремя: {sw.Elapsed}");
    }

    [SkippableFact]
    public async Task Timeout_marks_timed_out()
    {
        Skip.If(OperatingSystem.IsWindows(), "спайк-тесты рассчитаны на Unix-инструменты");
        var r = await ProcessRunner.RunAsync("sleep", ["30"], Path.GetTempPath(), TimeSpan.FromMilliseconds(200));
        Assert.True(r.TimedOut);
        Assert.False(r.Cancelled);
    }
}
