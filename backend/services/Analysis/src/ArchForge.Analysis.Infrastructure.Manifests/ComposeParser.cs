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
    public static ComposeFile Parse(string yamlText)
    {
        if (new DeserializerBuilder().Build().Deserialize<object?>(yamlText) is not IDictionary<object, object> root)
            return new ComposeFile(null, []);

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

    // ports: короткая форма ("host:cont", "ip:host:cont", "cont", с /proto) и длинная ({target, published, host_ip}).
    // Published=true → порт опубликован на хост (доступен снаружи); это вход для детектора «порт наружу в prod» (E9.2).
    private static IReadOnlyList<PortMapping> ReadPorts(object? ports)
    {
        var list = new List<PortMapping>();
        if (ports is not IEnumerable<object> items) return list;
        foreach (var item in items)
        {
            if (item is IDictionary<object, object> map) // длинная форма
            {
                var target = Str(map, "target");
                if (string.IsNullOrEmpty(target)) continue;
                var published = Str(map, "published");
                var hostIp = Str(map, "host_ip");
                list.Add(new PortMapping(target,
                    string.IsNullOrEmpty(published) ? null : published,
                    string.IsNullOrEmpty(hostIp) ? null : hostIp,
                    Published: !string.IsNullOrEmpty(published)));
                continue;
            }
            var s = Interpolation.Resolve(item?.ToString());
            if (string.IsNullOrWhiteSpace(s)) continue;
            var slash = s.IndexOf('/'); if (slash >= 0) s = s[..slash]; // отбросить /tcp|/udp
            var parts = s.Split(':');
            string? hostIp2 = null, host = null; string container;
            switch (parts.Length)
            {
                case 1: container = parts[0]; break;                       // "8080" (публикуется на эфемерный хост-порт)
                case 2: host = parts[0]; container = parts[1]; break;      // "8080:80"
                default: hostIp2 = parts[0]; host = parts[1]; container = parts[2]; break; // "127.0.0.1:8080:80"
            }
            list.Add(new PortMapping(container.Trim(), host, hostIp2, Published: true));
        }
        return list;
    }

    // healthcheck: наличие секции (и не отключён `disable: true`).
    private static bool ReadHealthcheck(object? hc) =>
        hc is IDictionary<object, object> map &&
        !string.Equals(Str(map, "disable"), "true", StringComparison.OrdinalIgnoreCase);

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
