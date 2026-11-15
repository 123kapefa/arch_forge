using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Раскладка по папкам и именам файлов (методичка «Структура папок»). Код НЕ разбирается: только пути и имена .cs
// (общий безопасный обход: без симлинков, потолки на число и размер файлов), поэтому у источника нет нагрузки парсера.
// Папка с тех. названием: Common/Utils/Util/Utilities/Misc/Helpers/Helper/Managers/Services/Resolvers/Processors — всегда;
// Commands/Queries — если выше нет папки Features (внутри Features/{Entity}/Queries это и есть правильная раскладка).
// Файл с тех. именем: оканчивается на Manager/Helper/Resolver/Processor/Util(s)/Utility (Postprocessor — не считается: слово с заглавной).
public sealed class RoslynFolderSource : IFolderSource
{
    private const int MaxSitesPerKind = 1_000;      // детектор показывает не больше 1000 пунктов: больше в кеше держать незачем
    private const int MaxNameLength = 200;

    private static readonly HashSet<string> AlwaysFolders = new(StringComparer.OrdinalIgnoreCase)
        { "Common", "Utils", "Util", "Utility", "Utilities", "Misc", "Helpers", "Helper", "Manager", "Managers", "Services", "Resolver", "Resolvers", "Processor", "Processors" };
    private static readonly HashSet<string> FeatureOnlyFolders = new(StringComparer.OrdinalIgnoreCase) { "Commands", "Queries" };
    private static readonly string[] BadSuffixes = ["Manager", "Helper", "Resolver", "Processor", "Util", "Utils", "Utility", "Utilities", "Managers", "Helpers", "Resolvers", "Processors"];

    // Принятые в методичке абстракции с «Manager» в имени (ITransactionManager) и стандартные типы ASP.NET Identity: не переименовываем
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal) { "TransactionManager", "UserManager", "RoleManager", "SignInManager", "DependencyResolver" };

    private static string Clean(string s)
    {
        var t = s.Length > MaxNameLength ? s[..MaxNameLength] + "…" : s;
        return new string(t.Where(c => !(char.IsControl(c) || c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩')).ToArray());
    }

    public FolderFacts Read(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return FolderFacts.Empty;
        var folders = new Dictionary<string, FolderSite>(StringComparer.Ordinal);
        var names = new List<FolderSite>();
        // порядок обхода не гарантирован: сортируем, чтобы срез по потолку, порядок и пример-файл папки были одинаковыми от запуска к запуску
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var layer = RoslynScan.Layer(file, root);
            if (layer is "tests" or "Contracts" or "Shared") continue;
            var path = file.Replace('\\', '/');
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var segments = rel.Split('/');                                          // последний — файл
            var prefixLen = 0;                                                      // длина пути до текущего сегмента (без Join на каждый файл)
            var featuresIdx = -1;
            for (var i = 0; i < segments.Length - 1 && folders.Count < MaxSitesPerKind; i++)
            {
                var seg = segments[i];
                var start = prefixLen;
                prefixLen += seg.Length + 1;
                if (featuresIdx < 0 && seg.Equals("Features", StringComparison.OrdinalIgnoreCase)) featuresIdx = i;
                // Services в слое Infrastructure — адаптеры внешних систем, не «бакет по тех. признаку»; Commands/Queries правильны только в Features/{Entity}/
                var tech = AlwaysFolders.Contains(seg) && !(seg.Equals("Services", StringComparison.OrdinalIgnoreCase) && layer == "Infrastructure")
                    || FeatureOnlyFolders.Contains(seg) && !(featuresIdx >= 0 && i > featuresIdx + 1);
                if (!tech) continue;
                var folder = rel[..(start + seg.Length)];
                if (!folders.ContainsKey(folder)) folders[folder] = new FolderSite(Clean(folder), path, layer);   // Clean только для нового ключа
            }
            var stem = Path.GetFileNameWithoutExtension(file);
            var core = stem.Length > 1 && stem[0] == 'I' && char.IsUpper(stem[1]) ? stem[1..] : stem;       // ITransactionManager → TransactionManager
            if (names.Count < MaxSitesPerKind && !AllowedNames.Contains(core) && BadSuffixes.Any(s => stem.EndsWith(s, StringComparison.Ordinal)))
                names.Add(new FolderSite(Clean(stem), path, layer));
        }
        return new FolderFacts([.. folders.Values.OrderBy(f => f.Name, StringComparer.Ordinal)], names);
    }
}
