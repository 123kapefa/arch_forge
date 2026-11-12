using System.Diagnostics;
using System.Text;

namespace ArchForge.Agent.Infrastructure;

/// <summary>Итог запуска внешнего процесса.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Cancelled);

// Безопасный запуск внешних команд:
//  • аргументы списком (ArgumentList), UseShellExecute=false — оболочка не интерпретирует аргументы,
//    поэтому инъекция через shell-метасимволы в промпте невозможна (adr/0005);
//  • таймаут и отмена убивают всё дерево процессов (паттерн CancellationToken из P1-H).
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg); // каждый аргумент — отдельный элемент, не shell-строка

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            bool timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            TryKillTree(process);
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), timedOut, !timedOut);
        }

        process.WaitForExit(); // синхронный доужид флашит хвосты OutputDataReceived/ErrorDataReceived
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false, false);
    }

    private static void TryKillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* процесс уже мёртв/недоступен — гасим гонку */ }
    }
}
