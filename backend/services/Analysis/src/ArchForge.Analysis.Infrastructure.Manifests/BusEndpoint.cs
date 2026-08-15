namespace ArchForge.Analysis.Infrastructure.Manifests;

// Разбор подключения к шине из config-ключей вне connection strings (RabbitMQ__Host, Kafka__BootstrapServers…).
// Выделен отдельным internal-парсером (как ConnectionString/Interpolation) — тестируется напрямую [Theory].
internal static class BusEndpoint
{
    // Известные шины как СЕГМЕНТ config-ключа (RabbitMQ__Host, MassTransit__RabbitMq__Host, Kafka__BootstrapServers).
    private static readonly string[] KeyTokens =
        { "rabbitmq", "rabbit", "kafka", "redpanda", "nats", "amqp", "servicebus", "eventbus", "messagebus", "masstransit" };

    // Ключ указывает на ХОСТ шины: первый сегмент — известная шина, последний — host/broker-подобное поле.
    // Ложные срабатывания отсекает финальный фильтр «резолвится в локальный Topic» на месте вызова.
    public static bool IsHostKey(string key)
    {
        var segs = key.Split("__", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0 || !KeyTokens.Contains(segs[0].ToLowerInvariant())) return false;
        return segs[^1].ToLowerInvariant() is "host" or "hostname" or "bootstrapservers" or "brokers"
            or "server" or "servers" or "endpoint" or "url" or "uri" or "connection" or "connectionstring";
    }

    // Хост шины из значения: «amqp://user:pass@rabbitmq:5672» / «kafka1:9092,kafka2:9092» / «rabbitmq» → «rabbitmq».
    // null — пусто / ${VAR} / IP-литерал / localhost / bind-паттерн.
    public static string? Host(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        var scheme = v.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) v = v[(scheme + 3)..];   // отрезаем amqp:// / kafka://
        v = v.Split(',')[0];                       // несколько брокеров → первый
        var at = v.LastIndexOf('@');
        if (at >= 0) v = v[(at + 1)..];            // userinfo
        v = v.Split('/', '?')[0];
        if (v.StartsWith("[", StringComparison.Ordinal)) return null; // IPv6-литерал
        var host = v.Split(':')[0];                // отрезаем порт
        if (host.Length == 0 || host.Contains("${", StringComparison.Ordinal)) return null;
        if (System.Net.IPAddress.TryParse(host, out _)) return null;
        host = host.Split('.')[0];                 // DNS-хвост: rabbitmq.namespace → rabbitmq
        return host is "localhost" or "+" or "*" ? null : host;
    }
}
