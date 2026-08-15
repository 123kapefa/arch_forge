namespace ArchForge.Analysis.Infrastructure.Manifests;

// Разбор URL-значений конфигурации в имя сервиса-цели http-call. Общий для compose-env и appsettings-детектора.
internal static class HttpUrls
{
    public static bool IsUrlKey(string key) =>
        key.Contains("url", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("uri", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("baseaddress", StringComparison.OrdinalIgnoreCase);

    // Известные публичные домены (точным суффиксом, а не голым TLD — иначе внутренние зоны .io/.dev терялись бы).
    private static readonly string[] PublicDomains =
        { ".cloudflare.com", ".githubusercontent.com", ".github.com", ".googleapis.com", ".amazonaws.com", ".windows.net" };

    // Хост сервиса из URL-значения: «http://audit-api.product-site:8080» → «audit-api».
    // null — не http-URL / userinfo / IP-литерал / localhost / инфра / bind-паттерн / известный публичный домен.
    public static string? Host(string? value)
    {
        if (value is null) return null;
        var v = value.Trim();
        var sep = v.IndexOf("//", StringComparison.Ordinal);
        if (sep < 0 || !(v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return null;

        var authority = v[(sep + 2)..].Split('/', '?')[0];
        var at = authority.LastIndexOf('@');                                    // userinfo (user:pass@host) — отрезаем
        if (at >= 0) authority = authority[(at + 1)..];
        if (authority.StartsWith("[", StringComparison.Ordinal)) return null;   // IPv6-литерал — не сервис

        var fullHost = authority.Split(':')[0];
        if (fullHost.Length == 0 || fullHost.Contains("${", StringComparison.Ordinal)) return null;
        if (System.Net.IPAddress.TryParse(fullHost, out _)) return null;        // IPv4-литерал — не сервис

        var lower = fullHost.ToLowerInvariant();
        foreach (var pub in PublicDomains)
            if (lower.EndsWith(pub, StringComparison.Ordinal)) return null;

        var host = fullHost.Split('.')[0];                                      // k8s-DNS: audit-api.product-site → audit-api
        // "+"/"*" — bind-паттерны ASPNETCORE_URLS (http://+:8080); localhost/инфра — не сервис.
        if (host is "localhost" or "minio" or "+" or "*") return null;
        return host;
    }
}
