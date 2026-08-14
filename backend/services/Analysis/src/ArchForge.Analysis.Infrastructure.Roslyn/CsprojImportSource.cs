using System.Xml.Linq;
using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Прямые библиотеки из <PackageReference> в .csproj таргета. Работает без сборки/restore.
// Версию берём из атрибута/дочернего элемента, а при central package management —
// из Directory.Packages.props (<PackageVersion Include Version>).
public sealed class CsprojImportSource : IImportSource
{
    public IReadOnlyList<PackageRef> Read(string path)
    {
        var central = ReadCentralVersions(path);
        var result = new List<PackageRef>();
        foreach (var csproj in Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories))
        {
            string project = Path.GetFileNameWithoutExtension(csproj);
            XDocument doc;
            try { doc = XDocument.Load(csproj); }
            catch { continue; }

            foreach (var pr in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                string? name = pr.Attribute("Include")?.Value ?? pr.Attribute("Update")?.Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim();

                string version = pr.Attribute("Version")?.Value
                    ?? pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                    ?? (central.TryGetValue(name, out var cv) ? cv : "");
                result.Add(new PackageRef(name, version.Trim(), project));
            }
        }
        return result;
    }

    // Central Package Management: Directory.Packages.props → имя пакета → версия.
    private static Dictionary<string, string> ReadCentralVersions(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var props in Directory.EnumerateFiles(path, "Directory.Packages.props", SearchOption.AllDirectories))
        {
            XDocument doc;
            try { doc = XDocument.Load(props); }
            catch { continue; }
            foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
            {
                string? name = pv.Attribute("Include")?.Value;
                string? version = pv.Attribute("Version")?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                    map[name.Trim()] = version.Trim();
            }
        }
        return map;
    }
}
