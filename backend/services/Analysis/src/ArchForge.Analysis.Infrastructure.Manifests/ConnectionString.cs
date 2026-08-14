namespace ArchForge.Analysis.Infrastructure.Manifests;

// Достаём host из строки подключения:
//   Npgsql/SqlServer: "Host=postgres-auth;Port=5432;…"  (или Server=/Data Source=)
//   Redis:            "redis-auth:6379,password=…"
// Отложенный целиком (`${KEY}`) возвращаем как есть — вызывающий пометит ребро Deferred и сделает внешний узел.
internal static class ConnectionString
{
    private static readonly string[] HostKeys = { "Host=", "Server=", "Data Source=" };

    public static string? ExtractHost(string? conn)
    {
        if (string.IsNullOrWhiteSpace(conn)) return null;
        conn = conn.Trim();

        // весь коннекшн отложен: ${CATALOG_DB_CONNECTION}
        if (conn.StartsWith("${", StringComparison.Ordinal) && conn.EndsWith("}", StringComparison.Ordinal) && !conn.Contains(';'))
            return conn;

        foreach (var part in conn.Split(';'))
        {
            var t = part.Trim();
            foreach (var key in HostKeys)
                if (t.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    var host = t[key.Length..].Trim();
                    return string.IsNullOrEmpty(host) ? null : host;
                }
        }

        // формат "host:port,…" (redis)
        var head = conn.Split(',')[0].Trim();
        var colon = head.IndexOf(':');
        if (colon > 0 && !head.Contains('=')) return head[..colon].Trim();

        return null;
    }
}
