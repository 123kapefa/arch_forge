namespace ArchForge.Analysis.Infrastructure.Manifests;

// Discovery overlay-файлов рядом с base-compose (E9.1 слой 2): «{stem}.{env}.{yml|yaml}».
//  • env == "override" — дефолтный overlay: docker compose сам применяет его поверх base (без явного выбора);
//  • прочие env (dev/stage/prod) — именованные среды, применяются явным выбором (часть 2 слоя).
// stem = имя base до первой точки: docker-compose.yml → «docker-compose»; compose.yaml → «compose».
internal static class ComposeOverlays
{
    /// <summary>Токен дефолтного overlay (docker-compose.override.yml).</summary>
    public const string Default = "override";

    /// <summary>env-токен → путь overlay-файла рядом с base. Пусто, если оверлеев нет.</summary>
    public static IReadOnlyDictionary<string, string> Discover(string basePath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dir = Path.GetDirectoryName(basePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;

        var stem = Path.GetFileName(basePath).Split('.')[0];
        // Порядок ФС не гарантирован → сортируем, чтобы при коллизии {env}.yml vs {env}.yaml выбор был детерминирован (первый).
        foreach (var f in Directory.EnumerateFiles(dir, $"{stem}.*").OrderBy(x => x, StringComparer.Ordinal))
        {
            if (string.Equals(f, basePath, StringComparison.Ordinal)) continue;
            var parts = Path.GetFileName(f).Split('.');
            if (parts.Length != 3) continue;                       // ждём ровно {stem}.{env}.{ext}
            if (parts[2].ToLowerInvariant() is not ("yml" or "yaml")) continue;
            result.TryAdd(parts[1], f);                            // env-токен → путь; первый по сортировке выигрывает
        }
        return result;
    }
}
