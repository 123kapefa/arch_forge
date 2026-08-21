namespace ArchForge.Analysis.Infrastructure.Manifests;

// Достаём host из строки подключения:
//   Npgsql/SqlServer: "Host=postgres-auth;Port=5432;…"  (или Server=/Data Source=)
//   Redis:            "redis-auth:6379,password=…"
//   URL-форма (G6):   "amqp://user:pass@rabbitmq:5672" · "redis://cache:6379" · "mongodb://mongo:27017"
// Отложенный целиком (`${KEY}`) возвращаем как есть — вызывающий пометит ребро Deferred и сделает внешний узел.
// Безопасность: userinfo (логин:пароль) НИКОГДА не возвращаем — только host (secrets-config).
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

        // URL-форма (scheme://[user:pass@]host[:port][/…]): amqp/redis/mongodb/postgres/… (G6).
        var scheme = conn.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var rest = conn[(scheme + 3)..];
            // userinfo (логин:пароль) отрезаем по ПОСЛЕДНЕМУ '@' и ДО разбора пути — иначе '/' или '@'
            // внутри пароля (base64: aB3/xY9, p@ss) обрезали бы authority раньше времени. Пароль не тащим (secrets-config).
            var at = rest.LastIndexOf('@');
            var authority = (at >= 0 ? rest[(at + 1)..] : rest).Split('/', '?', ',')[0];
            if (authority.StartsWith("[", StringComparison.Ordinal)) return null; // IPv6-литерал
            var h = authority.Split(':')[0].Trim();                     // отрезаем порт
            if (h.Length == 0) return null;
            if (h.Contains("${", StringComparison.Ordinal)) return h;   // частично отложен → вернуть как есть (Deferred, как ветка Host=)
            return System.Net.IPAddress.TryParse(h, out _) || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? null : h;
        }

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
