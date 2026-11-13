using System.Xml;
using System.Xml.Linq;
using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Клиентские библиотеки сервиса (E3.1): проекты ВНЕ папки сервиса, на которые есть ProjectReference, и в которых есть метод
// регистрации HTTP-клиента (AddHttpClient/AddRefitClient/RestService.For), вызываемый кодом самого сервиса. Одной ссылки мало:
// библиотека может быть подключена ради DTO, тогда клиентом сервис не пользуется и вызовов у него нет.
// Только прямые ссылки (транзитивные библиотеки не прослеживаем). Синтаксис и XML — без сборки проекта и без сети.
// Защита от ложных приписываний: библиотека — проект с «клиентским» именем (*.Contracts, *.Client…); метод регистрации — статический
// метод-расширение `this IServiceCollection` с AddHttpClient/AddRefitClient/RestService.For внутри; имя метода, объявленное в нескольких
// библиотеках или в самом сервисе, неоднозначно и не засчитывается.
public sealed class CsprojClientLibraryResolver : IClientLibraryResolver
{
    private const int MaxCsprojs = 300, MaxLibraries = 64, MaxCsPerLibrary = 3000;

    // Папка проекта называется как клиентская библиотека. Общие библиотеки (Authentication, Core, Shared…) сюда не попадают:
    // их AddHttpClient обслуживает другие нужды, а не связь с сервисом-владельцем.
    private static readonly string[] ClientSuffixes = [".Contracts", ".Client", ".Clients", ".Sdk", ".HttpClient", ".HttpClients", ".Communication"];
    private static bool IsClientLibraryName(string dirName) => ClientSuffixes.Any(s => dirName.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    // Методы регистрации по библиотеке: не разбираем библиотеку заново на каждого потребителя (отпечаток = файлы + время записи).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Fp, string[] Names)> Memo = new(StringComparer.Ordinal);

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,     // симлинки на папки не обходим (#158)
    };

    public IReadOnlyList<ClientLibrary> Resolve(string consumerRoot, string systemRoot, CancellationToken ct = default)
    {
        if (!Directory.Exists(consumerRoot) || string.IsNullOrWhiteSpace(systemRoot)) return [];
        var root = Path.GetFullPath(consumerRoot).TrimEnd('/', '\\');
        var sysRoot = Path.GetFullPath(systemRoot).TrimEnd('/', '\\');
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // 1. ProjectReference на csproj вне папки сервиса → папки библиотек
        var libDirs = new SortedSet<string>(StringComparer.Ordinal);
        var seen = 0;
        foreach (var csproj in EnumerateProjects(root))
        {
            ct.ThrowIfCancellationRequested();
            if (IsNoise(csproj)) continue;
            if (++seen > MaxCsprojs) break;
            foreach (var include in ProjectReferences(csproj))
            {
                string target;
                try { target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, include.Replace('\\', '/'))); }
                catch { continue; }
                // ДО обращения к файловой системе: библиотека обязана лежать внутри корня анализа (иначе враждебный csproj с абсолютным
                // или «../../..» путём увёл бы чтение и разбор за пределы проекта; на Windows File.Exists по UNC открыл бы SMB-соединение)
                if (!target.StartsWith(sysRoot + Path.DirectorySeparatorChar, cmp)) continue;
                if (!target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                if (target.StartsWith(root + Path.DirectorySeparatorChar, cmp)) continue;      // свой проект — не библиотека другого сервиса
                var libDir = Path.GetDirectoryName(target)!;
                if (IsClientLibraryName(Path.GetFileName(libDir))) libDirs.Add(libDir);
                if (libDirs.Count >= MaxLibraries) break;
            }
            if (libDirs.Count >= MaxLibraries) break;
        }
        if (libDirs.Count == 0) return [];

        // 2. в библиотеке: методы регистрации клиентов
        var registrations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // метод → библиотеки (папки), где он объявлен
        foreach (var dir in libDirs)
            foreach (var name in RegistrationMethods(dir, ct))
            {
                if (!registrations.TryGetValue(name, out var set)) registrations[name] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(dir);
            }
        foreach (var ambiguous in registrations.Where(r => r.Value.Count > 1).Select(r => r.Key).ToList()) registrations.Remove(ambiguous);   // одно имя в нескольких библиотеках
        if (registrations.Count == 0) return [];

        // 3. сервис вызывает хотя бы один такой метод
        var used = new HashSet<string>(StringComparer.Ordinal);
        var declaredHere = new HashSet<string>(StringComparer.Ordinal);                  // такое же имя объявлено в самом сервисе — не их метод
        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _))
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var candidates = registrations.Keys.Where(n => text.Contains(n, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0) continue;
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            foreach (var m in tree.DescendantNodes().OfType<MethodDeclarationSyntax>())
                if (registrations.ContainsKey(m.Identifier.Text)) declaredHere.Add(m.Identifier.Text);
            foreach (var inv in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (RoslynHttpCallSource.CalleeName(inv) is { } n && registrations.TryGetValue(n, out var dirs))
                    foreach (var d in dirs) used.Add(d + "\0" + n);
        }

        // метод засчитывается, только если он вызван и НЕ объявлен в самом сервисе (одноимённый AddInfrastructure и т. п.)
        var usedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in used)
        {
            var parts = key.Split('\0');
            if (!declaredHere.Contains(parts[1])) usedDirs.Add(parts[0]);
        }
        return libDirs.Where(usedDirs.Contains).Select(d => new ClientLibrary(Path.GetFileName(d), d)).ToList();
    }

    // .csproj под корнем сервиса; каталоги bin/obj/node_modules/.git не обходим вовсе (а не фильтруем результат после обхода).
    private static IEnumerable<string> EnumerateProjects(string root) =>
        new System.IO.Enumeration.FileSystemEnumerable<string>(root, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath(), Walk)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !e.IsDirectory && e.FileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase),
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry e) =>
                !(e.FileName.Equals("bin", StringComparison.OrdinalIgnoreCase) || e.FileName.Equals("obj", StringComparison.OrdinalIgnoreCase)
                  || e.FileName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || e.FileName.Equals(".git", StringComparison.OrdinalIgnoreCase)),
        };

    private static IEnumerable<string> ProjectReferences(string csproj)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(csproj, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            doc = XDocument.Load(reader);
        }
        catch { return []; }                                  // битый csproj — пропускаем
        return doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include")).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!).ToList();
    }

    // Методы-расширения `this IServiceCollection` библиотеки, в теле которых есть регистрация клиента.
    private static IEnumerable<string> RegistrationMethods(string libDir, CancellationToken ct)
    {
        var files = RoslynScan.EnumerateCsCapped(libDir, out _).OrderBy(f => f, StringComparer.Ordinal).Take(MaxCsPerLibrary).ToList();
        long fp = files.Count;
        foreach (var f in files)
            try { var fi = new FileInfo(f); fp = unchecked(fp * 31 + fi.LastWriteTimeUtc.Ticks + fi.Length); } catch { /* файл исчез */ }
        if (Memo.TryGetValue(libDir, out var memo) && memo.Fp == fp) return memo.Names;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            if (!text.Contains("AddHttpClient", StringComparison.Ordinal) && !text.Contains("AddRefitClient", StringComparison.Ordinal)
                && !text.Contains("RestService", StringComparison.Ordinal)) continue;
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            foreach (var m in tree.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (m.Body is null && m.ExpressionBody is null) continue;
                var first = m.ParameterList.Parameters.FirstOrDefault();
                if (first is null || !first.Modifiers.Any(SyntaxKind.ThisKeyword) || first.Type?.ToString().EndsWith("IServiceCollection", StringComparison.Ordinal) != true) continue;
                if (m.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsClientRegistration)) names.Add(m.Identifier.Text);
            }
        }
        var result = names.ToArray();
        Memo[libDir] = (fp, result);
        return result;
    }

    private static bool IsClientRegistration(InvocationExpressionSyntax inv) =>
        RoslynHttpCallSource.CalleeName(inv) is "AddHttpClient" or "AddRefitClient"
        || (RoslynHttpCallSource.CalleeName(inv) == "For" && inv.Expression is MemberAccessExpressionSyntax { Expression: var q } && q.ToString().EndsWith("RestService", StringComparison.Ordinal));

    private static bool IsNoise(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.Ordinal) || p.Contains("/obj/", StringComparison.Ordinal)
            || p.Contains("/node_modules/", StringComparison.Ordinal) || p.Contains("/.git/", StringComparison.Ordinal);
    }
}
