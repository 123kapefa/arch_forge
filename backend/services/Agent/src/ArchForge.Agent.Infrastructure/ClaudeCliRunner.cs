using ArchForge.Agent.Domain;

namespace ArchForge.Agent.Infrastructure;

/// <summary>
/// Настройки Claude CLI адаптера: исполняемый файл + доп. аргументы. Флаг автономии прав вынесен
/// в конфиг (ExtraArgs), а не зашит в код — permission-posture решается при развёртывании (adr/0005).
/// </summary>
public sealed record ClaudeCliOptions(string Executable = "claude", IReadOnlyList<string>? ExtraArgs = null);

/// <summary>
/// Адаптер <see cref="IAgentRunner"/> для Claude CLI (headless <c>claude -p "&lt;инструкция&gt;"</c>).
/// Модель «автономия + git diff-gate»: агент правит рабочую папку свободно, безопасность — в обратимости
/// (pre-flight требует git-репо и чистое дерево; <see cref="GitWorkspace"/> снимает diff и делает откат).
/// </summary>
public sealed class ClaudeCliRunner(GitWorkspace git, ClaudeCliOptions? options = null) : IAgentRunner
{
    private readonly ClaudeCliOptions _opts = options ?? new ClaudeCliOptions();

    public string ProviderId => "claude";

    /// <summary>Аргументы: инструкция — ЦЕЛЬНЫЙ аргумент после -p (не склейка в shell → инъекция невозможна).</summary>
    public IReadOnlyList<string> BuildArguments(string prompt)
    {
        var args = new List<string> { "-p", prompt };
        if (_opts.ExtraArgs is { Count: > 0 }) args.AddRange(_opts.ExtraArgs);
        return args;
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        // Pre-flight: без него diff/откат затрут чужие незакоммиченные правки.
        if (!Directory.Exists(request.WorkingDirectory))
            return Fail("рабочая папка не существует");
        if (!await git.IsGitRepoAsync(request.WorkingDirectory, ct))
            return Fail("целевой проект не является git-репозиторием");
        if (!await git.IsCleanAsync(request.WorkingDirectory, ct))
            return Fail("рабочее дерево не чистое — закоммить или спрячь изменения перед прогоном");

        ProcessResult run;
        try
        {
            run = await ProcessRunner.RunAsync(
                _opts.Executable, BuildArguments(request.Prompt),
                request.WorkingDirectory, request.Timeout, ct);
        }
        catch (Exception ex)
        {
            return Fail($"не удалось запустить провайдера '{_opts.Executable}': {ex.Message}");
        }

        var changed = await git.ChangedFilesAsync(request.WorkingDirectory, ct);
        var diff = changed.Count > 0 ? await git.DiffAsync(request.WorkingDirectory, ct) : "";

        var status = run switch
        {
            { Cancelled: true } => AgentStatus.Cancelled,
            { TimedOut: true } => AgentStatus.TimedOut,
            { ExitCode: 0 } => AgentStatus.Succeeded,
            _ => AgentStatus.Failed,
        };
        string log = string.IsNullOrEmpty(run.StdErr) ? run.StdOut : $"{run.StdOut}\n{run.StdErr}";
        string? error = status == AgentStatus.Failed ? $"провайдер вернул код {run.ExitCode}" : null;
        return new AgentRunResult(status, changed, diff, log, run.ExitCode, error);
    }

    private static AgentRunResult Fail(string error) =>
        new(AgentStatus.Failed, [], "", "", null, error);
}
