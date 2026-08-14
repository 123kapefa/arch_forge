namespace ArchForge.Agent.Domain;

// Модель прогона ИИ-агента: запрос → результат. Факт изменений берём из git, не из «слова» провайдера.

public enum AgentStatus { Succeeded, Failed, TimedOut, Cancelled }

/// <summary>Изменённый прогоном файл: путь относительно корня репо + git-статус (напр. "M", "??", "D").</summary>
public sealed record ChangedFile(string Path, string ChangeKind);

/// <summary>Запрос на прогон агента в рабочей папке цели (корень git-репо валидируется адаптером).</summary>
public sealed record AgentRunRequest(string WorkingDirectory, string Prompt, TimeSpan Timeout);

/// <summary>Результат прогона: статус + что изменилось (из git) + unified-diff + лог провайдера.</summary>
public sealed record AgentRunResult(
    AgentStatus Status,
    IReadOnlyList<ChangedFile> ChangedFiles,
    string Diff,
    string Log,
    int? ExitCode,
    string? Error);
