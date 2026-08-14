using System.Xml.Linq;
using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Прямые библиотеки из <PackageReference> в .csproj таргета. Работает без сборки/restore.
// Версия: атрибут/дочерний элемент, а при central package management — из БЛИЖАЙШЕГО
// Directory.Packages.props (вверх по дереву от проекта, чтобы не смешивать независимые поддеревья).
// Внутри проекта Update переопределяет Include; условные (Condition) ссылки пропускаем.
//
// Известные ограничения XML-разбора (это не MSBuild evaluation) — TODO слайс 2 / assets.json:
//  • свойства не разворачиваются: Version="$(Foo)" попадёт как литерал;
//  • <GlobalPackageReference> (analyzers/SourceLink) не читается — только прямые зависимости;
//  • регистр имени пакета — по первому вхождению (порядок обхода файлов не гарантирован).
public sealed class CsprojImportSource : IImportSource
{
    private static readonly Dictionary<string, string> Empty = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PackageRef> Read(string path)
    {
        string root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var propsCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PackageRef>();

        foreach (var csproj in Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories))
        {
            string project = Path.GetFileNameWithoutExtension(csproj);
            XDocument doc;
            try { doc = XDocument.Load(csproj); }
            catch { continue; }

            var central = CentralVersionsFor(csproj, root, propsCache);

            // Внутри проекта: package → версия. Include ставит, Update переопределяет; условные — мимо.
            var perProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                if (pr.Attribute("Condition") is not null) continue; // не знаем, активна ли ссылка
                string? include = pr.Attribute("Include")?.Value;
                string? update = pr.Attribute("Update")?.Value;
                string? name = include ?? update;
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim();

                string version = pr.Attribute("Version")?.Value
                    ?? pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                    ?? (central.TryGetValue(name, out var cv) ? cv : "");

                bool isUpdate = include is null && update is not null;
                if (isUpdate || !perProject.ContainsKey(name))
                    perProject[name] = version.Trim();
            }

            foreach (var (name, version) in perProject)
                result.Add(new PackageRef(name, version, project));
        }
        return result;
    }

    // Ближайший Directory.Packages.props вверх по дереву от проекта (в пределах root).
    private static Dictionary<string, string> CentralVersionsFor(
        string csprojPath, string root, Dictionary<string, Dictionary<string, string>> cache)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath));
        while (dir is not null)
        {
            string props = Path.Combine(dir, "Directory.Packages.props");
            if (File.Exists(props))
            {
                if (!cache.TryGetValue(props, out var map))
                {
                    map = ParsePackageVersions(props);
                    cache[props] = map;
                }
                return map;
            }
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) break; // дошли до корня таргета
            dir = Path.GetDirectoryName(dir);
        }
        return Empty;
    }

    private static Dictionary<string, string> ParsePackageVersions(string propsPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XDocument doc;
        try { doc = XDocument.Load(propsPath); }
        catch { return map; }
        foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
        {
            string? name = pv.Attribute("Include")?.Value;
            string? version = pv.Attribute("Version")?.Value;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                map[name.Trim()] = version.Trim();
        }
        return map;
    }
}
