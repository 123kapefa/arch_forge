using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Папочный discovery: сервис = папка с .slnx/.sln (с фильтром шума). Компоуз сервиса ищем в его корне —
// поэтому подхватываются нестандартные имена (docker-compose-dev.yaml), а инфра/мониторинг-compose
// без .slnx сервисами НЕ считаются (закрывает находку спайка P1.5-0).
public sealed class FolderDiscoverySource : IServiceDiscoverySource
{
    public IReadOnlyList<DiscoveredService> Discover(string root)
    {
        if (File.Exists(root) || !Directory.Exists(root)) return []; // ждём корень системы (папку)

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var slns = Directory.EnumerateFiles(root, "*.sln*", opts)
            .Where(f => (f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))   // *.sln* ловит и .slnf
                        && !PathFilter.IsNoise(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)                                // детерминизм до дедупа по папке
            .ToList();

        var byRoot = new Dictionary<string, DiscoveredService>(StringComparer.OrdinalIgnoreCase);
        foreach (var sln in slns)
        {
            var svcRoot = Path.GetDirectoryName(sln);
            if (string.IsNullOrEmpty(svcRoot) || byRoot.ContainsKey(svcRoot)) continue; // одна папка = один сервис
            var name = Path.GetFileNameWithoutExtension(sln); // имя решения: AuthService, KalmykovGroup.Server
            byRoot[svcRoot] = new DiscoveredService(name, svcRoot, FindServiceCompose(svcRoot));
        }

        return byRoot.Values.OrderBy(s => s.Root, StringComparer.Ordinal).ToList();
    }

    // Compose сервиса: shallow-поиск в его корне; предпочитаем базовое имя, иначе первый (кроме .build/.shared).
    private static string? FindServiceCompose(string svcRoot)
    {
        var candidates = Directory.EnumerateFiles(svcRoot, "*compose*", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetExtension(f) is ".yml" or ".yaml")
            .Where(f => !Path.GetFileName(f).Contains(".build.", StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileName(f).Contains(".shared.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var baseCompose = candidates.FirstOrDefault(f =>
            Path.GetFileName(f).ToLowerInvariant() is "docker-compose.yml" or "docker-compose.yaml"
                or "compose.yml" or "compose.yaml");
        return baseCompose ?? candidates.FirstOrDefault();
    }
}
