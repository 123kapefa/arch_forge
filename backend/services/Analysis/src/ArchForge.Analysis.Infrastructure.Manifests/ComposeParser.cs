using YamlDotNet.Serialization;

namespace ArchForge.Analysis.Infrastructure.Manifests;

internal sealed record ComposeService(
    string Name,
    string? Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> DependsOn);

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
                    ReadDependsOn(Get(svc, "depends_on"))));
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

    private static object? Get(IDictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var v) ? v : null;

    private static string? Str(IDictionary<object, object> map, string key) =>
        Get(map, key)?.ToString();
}
