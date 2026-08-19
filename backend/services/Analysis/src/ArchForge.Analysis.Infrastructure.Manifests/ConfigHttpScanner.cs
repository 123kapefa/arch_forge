using System.Text.Json;
using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Infrastructure.Manifests;

// Детектор http-call из appsettings*.json: сервис-владелец (по имени в пути) → сервисы, чьи URL он держит в конфиге.
// Закрывает reverse-proxy/gateway (YARP «Services»-мапа: Catalog→http://catalog-api:8080) и любые config-вызовы —
// их Roslyn не видит (не код). Цель эмитим только если это ИЗВЕСТНЫЙ сервис скана (иначе шум внешних URL:
// telemetry, auth-issuer, CDN). amqp/бус — не http → сюда не попадают. Резолвит цель тот же ResolveHttpCalls.
internal static class ConfigHttpScanner
{
    public static IReadOnlyList<(string SourceId, string TargetHost)> Scan(
        string root, IReadOnlyCollection<SystemNode> nodes, CancellationToken ct)
    {
        var intents = new List<(string, string)>();
        if (!Directory.Exists(root)) return intents;

        var services = nodes.Where(n => n.Kind == SystemNodeKind.Service)
            .Select(n => (Norm: Normalize(n.Name), n.Id))
            .Where(x => x.Norm.Length >= 3)
            .ToList();
        var known = new HashSet<string>(nodes.Select(n => n.Name.ToLowerInvariant()), StringComparer.Ordinal);

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "appsettings*.json", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (PathFilter.IsNoise(root, file) || IsTestPath(file)) continue;

            var ownerId = OwnerOf(file, services);
            if (ownerId is null) continue;

            foreach (var value in JsonStringValues(file))
            {
                if (HttpUrls.Host(value) is not { } host) continue;
                if (!known.Contains(host.ToLowerInvariant())) continue; // только внутренние сервисы
                intents.Add((ownerId, host));
            }
        }
        return intents;
    }

    // Владелец файла = сервис, чьё нормализованное имя — самая длинная подстрока нормализованного пути папки.
    // Точное совпадение суффиксов (catalog-api ↔ .../Catalog/Catalog.API): без обрезки «api».
    private static string? OwnerOf(string file, List<(string Norm, string Id)> services)
    {
        var path = Normalize(Path.GetDirectoryName(file) ?? "");
        string? bestId = null;
        var bestLen = 0;
        foreach (var (norm, id) in services)
            if (norm.Length > bestLen && path.Contains(norm, StringComparison.Ordinal))
                (bestId, bestLen) = (id, norm.Length);
        return bestId;
    }

    private static IEnumerable<string> JsonStringValues(string file)
    {
        var doc = TryParse(file);
        if (doc is null) yield break;
        using (doc)
        {
            var stack = new Stack<JsonElement>();
            stack.Push(doc.RootElement);
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var p in el.EnumerateObject()) stack.Push(p.Value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var it in el.EnumerateArray()) stack.Push(it);
                        break;
                    case JsonValueKind.String:
                        if (el.GetString() is { } s) yield return s;
                        break;
                }
            }
        }
    }

    private static JsonDocument? TryParse(string file)
    {
        try { return JsonDocument.Parse(File.ReadAllText(file)); }
        catch { return null; }
    }

    private static bool IsTestPath(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        file.Contains(".Tests", StringComparison.OrdinalIgnoreCase);

    // Имя → нормализованная форма: только строчные буквы/цифры (убираем -, ., _, пробелы, разделители пути).
    private static string Normalize(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
