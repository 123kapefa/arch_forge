using ArchForge.Analysis.Core.Systems;
using YamlDotNet.Serialization;

namespace ArchForge.Analysis.Infrastructure.Manifests;

internal sealed record ComposeService(
    string Name,
    string? Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<PortMapping> Ports,
    bool HasHealthcheck,
    IReadOnlyList<string> Networks);

internal sealed record ComposeFile(string? Name, IReadOnlyList<ComposeService> Services);

// Разбор docker-compose (YamlDotNet: якоря/алиасы/блок-скаляры). Строковые значения интерполируем
// (${VAR:-def}). environment и depends_on поддерживают обе формы (map и list).
internal static class ComposeParser
{
    /// <summary>Разобрать YAML-текст в корневой словарь compose (для merge оверрайдов). null — если не словарь.</summary>
    public static IDictionary<object, object>? Deserialize(string yamlText) =>
        new DeserializerBuilder().Build().Deserialize<object?>(yamlText) as IDictionary<object, object>;

    public static ComposeFile Parse(string yamlText) =>
        Deserialize(yamlText) is { } root ? Parse(root) : new ComposeFile(null, []);

    // Разбор из уже готового корневого словаря — точка входа после merge base+override (E9.1 слой 2).
    public static ComposeFile Parse(IDictionary<object, object> root)
    {
        var name = Str(root, "name");
        var services = new List<ComposeService>();

        if (Get(root, "services") is IDictionary<object, object> svcMap)
            foreach (var kv in svcMap)
            {
                if (kv.Key?.ToString() is not { } svcName || kv.Value is not IDictionary<object, object> svc) continue;
                var image = Interpolation.Resolve(Str(svc, "image"));
                services.Add(new ComposeService(
                    svcName,
                    string.IsNullOrEmpty(image) ? null : image,
                    ReadEnv(Get(svc, "environment")),
                    ReadDependsOn(Get(svc, "depends_on")),
                    ReadPorts(Get(svc, "ports")),
                    ReadHealthcheck(Get(svc, "healthcheck")),
                    ReadNetworks(Get(svc, "networks"))));
            }

        return new ComposeFile(name, services);
    }

    private static IReadOnlyDictionary<string, string> ReadEnv(object? env)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (env)
        {
            case IDictionary<object, object> map:
                foreach (var kv in map)
                    if (kv.Key?.ToString() is { } k)
                        dict[k] = Interpolation.Resolve(kv.Value?.ToString());
                break;
            case IEnumerable<object> list:              // ["KEY=value", …]
                foreach (var item in list)
                {
                    if (item?.ToString() is not { } s) continue;
                    var eq = s.IndexOf('=');
                    if (eq > 0) dict[s[..eq]] = Interpolation.Resolve(s[(eq + 1)..]);
                }
                break;
        }
        return dict;
    }

    private static IReadOnlyList<string> ReadDependsOn(object? dep)
    {
        var list = new List<string>();
        switch (dep)
        {
            case IDictionary<object, object> map:       // {x: {condition: …}}
                foreach (var kv in map) if (kv.Key?.ToString() is { } k) list.Add(k);
                break;
            case IEnumerable<object> items:             // ["x", …]
                foreach (var it in items) if (it?.ToString() is { } s) list.Add(s);
                break;
        }
        return list;
    }

    // ports: короткая форма ("cont", "host:cont", "ip:host:cont", "[ipv6]:host:cont", с /proto)
    // и длинная ({target, published, host_ip}). Published=true → порт опубликован на хост (доступен
    // снаружи); это вход для детектора «порт наружу в prod» (E9.2). Значения интерполируем как везде.
    private static IReadOnlyList<PortMapping> ReadPorts(object? ports)
    {
        var list = new List<PortMapping>();
        if (ports is not IEnumerable<object> items) return list;
        foreach (var item in items)
        {
            if (item is IDictionary<object, object> map) // длинная форма
            {
                var target = Interpolation.Resolve(Str(map, "target"));
                if (string.IsNullOrEmpty(target)) continue;
                var published = Interpolation.Resolve(Str(map, "published"));
                var hostIp = Interpolation.Resolve(Str(map, "host_ip"));
                list.Add(new PortMapping(target,
                    string.IsNullOrEmpty(published) ? null : published,
                    string.IsNullOrEmpty(hostIp) ? null : hostIp,
                    Published: !string.IsNullOrEmpty(published)));
                continue;
            }
            var s = Interpolation.Resolve(item?.ToString());
            if (string.IsNullOrWhiteSpace(s)) continue;
            var slash = s.IndexOf('/'); if (slash >= 0) s = s[..slash]; // отбросить /tcp|/udp
            if (ParseShortPort(s) is { } pm) list.Add(pm);
        }
        return list;
    }

    // Короткая форма порта. IPv6 host_ip идёт в скобках: "[::1]:8080:80" — его нельзя резать по ':'
    // как IPv4, поэтому вытаскиваем [..] отдельно. Пустой контейнерный порт (битый файл) → пропускаем.
    private static PortMapping? ParseShortPort(string s)
    {
        string? hostIp = null;
        if (s.StartsWith('['))                       // "[ipv6]:host:cont" | "[ipv6]:cont"
        {
            var close = s.IndexOf(']');
            if (close <= 0) return null;
            hostIp = s[1..close];
            s = s[(close + 1)..].TrimStart(':');     // остаток: "host:cont" | "cont"
        }
        var parts = s.Split(':');
        string? host = null; string container;
        switch (parts.Length)
        {
            case 1: container = parts[0]; break;                              // "8080" (эфемерный хост-порт)
            case 2: host = parts[0]; container = parts[1]; break;             // "host:cont" (или остаток IPv6)
            default: hostIp = parts[0]; host = parts[1]; container = parts[2]; break; // IPv4 "ip:host:cont"
        }
        container = container.Trim();
        if (container.Length == 0) return null;      // невалидно (напр. "8080:") — не выдумываем
        return new PortMapping(container, string.IsNullOrEmpty(host) ? null : host, hostIp, Published: true);
    }

    // healthcheck: наличие секции и не отключён (`disable: true` либо `test: NONE`/`["NONE"]` — эквивалент).
    private static bool ReadHealthcheck(object? hc)
    {
        if (hc is not IDictionary<object, object> map) return false;
        if (string.Equals(Str(map, "disable"), "true", StringComparison.OrdinalIgnoreCase)) return false;
        var test = Get(map, "test");
        if (string.Equals(test?.ToString(), "NONE", StringComparison.Ordinal)) return false;
        if (test is IEnumerable<object> tl)          // ["NONE"] — первый элемент
        {
            using var e = tl.GetEnumerator();
            if (e.MoveNext() && e.Current?.ToString() == "NONE") return false;
        }
        return true;
    }

    // networks: список или map (берём ключи-имена сетей).
    private static IReadOnlyList<string> ReadNetworks(object? nets)
    {
        var list = new List<string>();
        switch (nets)
        {
            case IDictionary<object, object> map:
                foreach (var kv in map) if (kv.Key?.ToString() is { } k) list.Add(k);
                break;
            case IEnumerable<object> items:
                foreach (var it in items) if (it?.ToString() is { } s) list.Add(s);
                break;
        }
        return list;
    }

    private static object? Get(IDictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var v) ? v : null;

    private static string? Str(IDictionary<object, object> map, string key) =>
        Get(map, key)?.ToString();
}
