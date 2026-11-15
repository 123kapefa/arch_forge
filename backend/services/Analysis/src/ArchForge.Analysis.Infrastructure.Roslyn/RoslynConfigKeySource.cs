using System.Text.Json;
using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Ключи конфигурации сервиса (E9.3): что читает код и что объявлено в appsettings*.json. Чисто синтаксический разбор.
// Формы чтения (ключ — строковый литерал):
//   config["A:B"] · config.GetSection("A")["B"] · config.GetRequiredSection("A")["B"] · config.GetValue<T>("A:B")
//   config.GetConnectionString("n") → ConnectionStrings:n
// Получатель должен ПОХОДИТЬ на IConfiguration по точному имени (config/configuration/cfg/section…) — иначе словари и кеши
// (`cache["x"]`) давали бы ложные ключи.
// СЕКРЕТЫ (secrets-config): из кода берём имя ключа, из appsettings — только ИМЕНА свойств; строковые значения не читаются в модель.
// Защита от враждебного проекта: потолки на файлы/ключи/длину имени, глубину JSON; имена очищаются от управляющих символов.
public sealed class RoslynConfigKeySource(ScanCache? cache = null) : IConfigKeySource
{
    private const int MaxUses = 5_000;
    private const int MaxDeclared = 20_000;
    private const int MaxKeyLength = 200;
    private const long MaxJsonBytes = 2_000_000;
    private const int MaxJsonFiles = 50;                 // файлов appsettings*.json на сервис
    private const long MaxJsonTotalBytes = 10_000_000;   // суммарный объём разбираемого JSON на сервис

    // Платформенные ключи ASP.NET/хоста: у них есть значения по умолчанию, «не хватает» они не бывают.
    private static readonly string[] PlatformPrefixes = ["logging:", "aspnetcore", "dotnet", "kestrel:", "allowedhosts", "urls", "hosturls"];
    private static bool IsPlatform(string key) =>
        PlatformPrefixes.Any(p => ConfigNorm(key).StartsWith(p, StringComparison.Ordinal));
    private static string ConfigNorm(string key) => key.Trim().Replace("__", ":", StringComparison.Ordinal).ToLowerInvariant();

    // Ключи из КОДА кешируем по отпечатку дерева (дорогой разбор); appsettings читаем каждый раз — в отпечаток .json не входят,
    // а файл мал и правят его часто (устаревший кеш давал бы «не хватает ключа», который уже добавили).
    public ServiceConfigKeys Read(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return ServiceConfigKeys.Empty;
        var used = cache is null ? ReadUsed(root, ct) : cache.GetOrAdd("configkeys", root, () => ReadUsed(root, ct), ct);
        return new ServiceConfigKeys(used.List, ReadDeclared(root, ct));
    }

    private static UsedKeys ReadUsed(string root, CancellationToken ct)
    {
        var anchor = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('/', '\\')) ?? root;
        var used = new Dictionary<string, ConfigKeyUse>(StringComparer.Ordinal);   // первая точка ОБЯЗАТЕЛЬНОГО использования на ключ
        var optionalOnly = new HashSet<string>(StringComparer.Ordinal);            // ключи с запасным значением (`?? "x"`, GetValue<T>(k, default))

        foreach (var file in RoslynScan.EnumerateCsCapped(root, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (used.Count >= MaxUses) break;
            if (RoslynScan.Layer(file, root) is "tests") continue;
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            // быстрый отсев: без этих слов чтения конфигурации в файле нет
            if (!text.Contains("IConfiguration", StringComparison.Ordinal) && !text.Contains(".Configuration", StringComparison.Ordinal)) continue;
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            var rel = Path.GetRelativePath(anchor, file).Replace('\\', '/');
            var n = 0;
            foreach (var node in tree.DescendantNodes())
            {
                if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                if (KeyOf(node) is not { } read || !IsSafeKey(read.Key) || IsPlatform(read.Key)) continue;
                var key = read.Key;
                if (read.Optional) { if (!used.ContainsKey(key)) optionalOnly.Add(key); continue; }
                optionalOnly.Remove(key);
                used.TryAdd(key, new ConfigKeyUse(key, rel, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                if (used.Count >= MaxUses) break;
            }
        }
        return new UsedKeys(used.Values.OrderBy(u => u.Key, StringComparer.Ordinal).ToList());
    }

    private sealed record UsedKeys(IReadOnlyList<ConfigKeyUse> List);   // обёртка: ScanCache кеширует ссылочные типы

    private readonly record struct KeyRead(string Key, bool Optional);

    // Ключ, читаемый узлом (или null). Optional — у ключа есть запасное значение: его отсутствие не ошибка развёртывания.
    private static KeyRead? KeyOf(SyntaxNode node)
    {
        switch (node)
        {
            case ElementAccessExpressionSyntax ea when ea.ArgumentList.Arguments.Count == 1 && Lit(ea.ArgumentList.Arguments[0].Expression) is { } k:
                string? full = null;
                if (LooksLikeConfig(ea.Expression)) full = k;
                else if (ea.Expression is InvocationExpressionSyntax gs && SectionName(gs) is { } sec) full = sec + ":" + k;
                if (full is null) return null;
                // `config["X"] ?? "default"` — запасное значение (а `?? throw …` — наоборот, ключ обязателен)
                var fallback = ea.Parent is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression) && b.Left == ea && b.Right is not ThrowExpressionSyntax;
                return new KeyRead(full, fallback);
            case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax ma && LooksLikeConfig(ma.Expression):
                var name = ma.Name.Identifier.Text;
                var args = inv.ArgumentList.Arguments;
                // GetValue<T>("A:B") обязателен; GetValue<T>("A:B", default) — с запасным значением
                if (name == "GetValue" && args.Count >= 1 && Lit(args[0].Expression) is { } v) return new KeyRead(v, Optional: args.Count >= 2);
                if (name == "GetConnectionString" && args.Count == 1 && Lit(args[0].Expression) is { } cs) return new KeyRead("ConnectionStrings:" + cs, false);
                return null;
            default: return null;
        }
    }

    // config.GetSection("A") / GetRequiredSection("A") → "A"
    private static string? SectionName(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "GetSection" or "GetRequiredSection" } ma
            && LooksLikeConfig(ma.Expression) && inv.ArgumentList.Arguments.Count == 1
            ? Lit(inv.ArgumentList.Arguments[0].Expression) : null;

    private static string? Lit(ExpressionSyntax? e) =>
        e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

    // Получатель похож на IConfiguration по точному имени (config/configuration/cfg, с `_`/`this.`/`builder.Configuration`).
    // Имена вроде «section» не берём: `section["x"]` слишком часто словарь/форма, а настоящие секции приходят через GetSection(...).
    private static bool LooksLikeConfig(ExpressionSyntax? e)
    {
        var name = e switch
        {
            IdentifierNameSyntax i => i.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return false;
        name = name.TrimStart('_');
        return name.Equals("config", StringComparison.OrdinalIgnoreCase) || name.Equals("configuration", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cfg", StringComparison.OrdinalIgnoreCase);
    }

    // Имя ключа из чужого кода: короткое, без пробелов/управляющих/bidi-символов — иначе не имя ключа, а мусор или подделка отображения.
    private static bool IsSafeKey(string k) =>
        k.Length is > 0 and <= MaxKeyLength && k.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
            && !(c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩'));

    // appsettings*.json сервиса → ИМЕНА свойств («A:B») по токену среды. Значения не сохраняются.
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> ReadDeclared(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var total = 0;
        var files = 0;
        long bytes = 0;
        foreach (var file in SafeFiles.Enumerate(root, "appsettings*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (total >= MaxDeclared || files >= MaxJsonFiles || bytes >= MaxJsonTotalBytes) break;   // потолки: враждебное дерево не раздувает работу
            if (PathFilter.IsNoise(root, file)) continue;
            var parts = Path.GetFileName(file).Split('.');
            // appsettings.json | appsettings.<Env>.json (иное — appsettings.Prod.Local.json — не трогаем: среда неоднозначна)
            string token;
            if (parts.Length == 2) token = "";
            else if (parts.Length == 3) token = parts[1].ToLowerInvariant();
            else continue;
            try
            {
                var len = new FileInfo(file).Length;
                if (len > MaxJsonBytes) continue;
                files++; bytes += len;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
                var set = result.TryGetValue(token, out var ex) ? (HashSet<string>)ex : new HashSet<string>(StringComparer.Ordinal);
                Flatten(doc.RootElement, "", set, ref total);
                result[token] = set;
            }
            catch { /* битый/нечитаемый json пропускаем (изоляция) */ }
        }
        return result;
    }

    private static void Flatten(JsonElement e, string prefix, HashSet<string> into, ref int total)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            if (total >= MaxDeclared) return;
            var key = prefix.Length == 0 ? p.Name : prefix + ":" + p.Name;
            if (key.Length > MaxKeyLength) continue;
            into.Add(Core.Systems.ConfigKeys.Normalize(key));
            total++;
            Flatten(p.Value, key, into, ref total);   // значение скаляра не читаем: смотрим только вид узла
        }
    }
}
