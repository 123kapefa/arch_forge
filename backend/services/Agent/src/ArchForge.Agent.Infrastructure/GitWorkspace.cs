using ArchForge.Agent.Domain;

namespace ArchForge.Agent.Infrastructure;

// git-safety петля вокруг прогона агента: репозиторий ли, чистое ли дерево, что изменилось,
// unified-diff и точечный откат к HEAD. Всё через git списком аргументов (без shell).
// Это страховка модели «автономия + diff-gate»: правки агента всегда обратимы.
public sealed class GitWorkspace
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    public async Task<bool> IsGitRepoAsync(string dir, CancellationToken ct = default)
    {
        var r = await Git(dir, ct, "rev-parse", "--is-inside-work-tree");
        return r.ExitCode == 0 && r.StdOut.Trim() == "true";
    }

    public async Task<bool> IsCleanAsync(string dir, CancellationToken ct = default)
    {
        var r = await Git(dir, ct, "status", "--porcelain");
        return r.ExitCode == 0 && string.IsNullOrWhiteSpace(r.StdOut);
    }

    /// <summary>Что изменилось после прогона — из git status --porcelain (правда с диска, не из отчёта провайдера).</summary>
    public async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(string dir, CancellationToken ct = default)
    {
        var r = await Git(dir, ct, "status", "--porcelain");
        var list = new List<ChangedFile>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // формат porcelain: "XY path"  (XY — статус: " M", "??", "A ", "D "…)
            if (line.Length < 4) continue;
            var kind = line[..2].Trim();
            var path = line[3..].Trim();
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal); // переименование "old -> new" — берём новое
            if (arrow >= 0) path = path[(arrow + 4)..];
            list.Add(new ChangedFile(path, kind));
        }
        return list;
    }

    /// <summary>Unified-diff рабочего дерева vs HEAD, включая новые файлы (через intent-to-add, затем снимаем метку).</summary>
    public async Task<string> DiffAsync(string dir, CancellationToken ct = default)
    {
        await Git(dir, ct, "add", "--intent-to-add", "--all");
        var r = await Git(dir, ct, "diff", "--no-color");
        await Git(dir, ct, "reset", "--quiet"); // снимаем intent-to-add, содержимое файлов не трогаем
        return r.StdOut;
    }

    /// <summary>Точечный откат к HEAD: tracked → restore, untracked → удалить файл. Не слепой git clean.</summary>
    public async Task RollbackAsync(string dir, IReadOnlyList<ChangedFile> changed, CancellationToken ct = default)
    {
        foreach (var f in changed)
        {
            if (f.ChangeKind == "??")
            {
                var full = Path.Combine(dir, f.Path);
                try { if (File.Exists(full)) File.Delete(full); } catch { /* best-effort */ }
            }
            else
            {
                await Git(dir, ct, "restore", "--staged", "--worktree", "--", f.Path);
            }
        }
    }

    private static Task<ProcessResult> Git(string dir, CancellationToken ct, params string[] args) =>
        ProcessRunner.RunAsync("git", args, dir, GitTimeout, ct);
}
