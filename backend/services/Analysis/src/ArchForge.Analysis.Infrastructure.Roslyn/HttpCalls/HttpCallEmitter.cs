using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpSyntax;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpClientCalls;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpWrapperCalls;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpBaseAddresses;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpConfigRefs;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpConfigSections;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpClientRegistry;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpRegisteredClients;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpCallEmitter;
using static ArchForge.Analysis.Infrastructure.Roslyn.HttpRefitCalls;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Превращение найденного вызова (verb + адрес) в HttpCallHit: абсолютные адреса (срез 1) и относительные пути по базовому адресу (срез 2).
internal static class HttpCallEmitter
{
    internal static void Add(List<HttpCallHit> hits, string verb, string? url, InvocationExpressionSyntax inv, ExpressionSyntax receiver, string path,
        IReadOnlyList<BaseAddr>? bases, ClientRegistry registry, FileCtx ctx, IReadOnlyDictionary<string, string> sections, bool wrapper = false)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url) || url.StartsWith('{')) return;                 // пусто или адрес начинается с подстановки — неизвестен
        if (StartsWithScheme(url) && Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return;   // ftp://, file:// …
            var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            // без query (секреты не тащим); подстановки {имя} сохраняем читаемыми, а не %7B…%7D (раскодируем только скобки)
            var clean = $"{abs.Scheme}://{abs.Authority}{UnescapeBraces(abs.AbsolutePath)}";
            hits.Add(new HttpCallHit(verb, clean, path, line, $"{verb} {clean}"));
            return;
        }
        // относительный путь: только похожий на URL-путь («/x» или «a/b»); голое слово — ключ кэша, имя файла, id — не вызов.
        if (!LooksLikePath(url) || bases is null) return;                          // в файле слишком много базовых адресов — тоже не гадаем
        var (found, ambiguous) = ResolveBase(inv, bases);
        if (found is null && !ambiguous)
            found = wrapper
                ? (inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } td ? ClientRegistry.Single(registry.Typed, td.Identifier.Text) : null)   // обёртка: адрес клиента — по классу
                : ResolveRegistered(inv, receiver, registry, ctx);   // конфликт в области реестром не «лечим»
        if (found is { } cb) EmitFromBase(hits, verb, url, inv, path, cb, sections, how: null);    // нет надёжного базового адреса — не угадываем
    }

    // «схема://…» в НАЧАЛЕ строки (http://, ftp://). «://» внутри query («/redirect?to=http://x») адресом не делает; на Unix путь «/x»
    // Uri.TryCreate сам считает абсолютным file-адресом, поэтому по одному TryCreate абсолютный адрес не отличить.
    internal static bool StartsWithScheme(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        if (i <= 0) return false;
        for (var k = 0; k < i; k++) if (!(char.IsAsciiLetterOrDigit(url[k]) || url[k] is '+' or '-' or '.')) return false;
        return char.IsAsciiLetter(url[0]);
    }

    // Протокол-относительные «//host/x», обратные слэши и «схема:значение» («user:42») относительными не считаем.
    // Голое слово без «/» — не путь (ключ кэша, имя файла, id).
    internal static bool LooksLikePath(string url) => IsSafeRelative(url) && (url.StartsWith('/') || url.Contains('/'));

    internal static bool IsSafeRelative(string url)
    {
        if (url.Any(char.IsControl)) return false;   // переводы строк, NUL — в путь вызова не берём (и в Evidence)
        var cut = url.IndexOfAny(['?', '#']);
        if (cut >= 0) url = url[..cut];          // «://» или «:» в query («/redirect?to=http://x») путь небезопасным не делает
        if (url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\') || url.Contains("://", StringComparison.Ordinal)) return false;
        var colon = url.IndexOf(':');
        return colon < 0 || (url.Contains('/') && url.IndexOf('/') < colon);
    }

    // Относительный путь + базовый адрес клиента → вызов: литерал даёт абсолютный адрес, ключ конфигурации — вызов с ключом
    // (значение подставит сборщик графа). `how` — пометка источника в Evidence (по умолчанию «по BaseAddress»).
    internal static void EmitFromBase(List<HttpCallHit> hits, string verb, string url, SyntaxNode lineAt, string path, ClientBase cb,
        IReadOnlyDictionary<string, string> sections, string? how, bool append = false)
    {
        var line = lineAt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (cb.Uri is not { } baseUri)
        {
            // адрес клиента — из конфигурации: значения не читаем, отдаём ключ и относительный путь; цель найдёт сборщик графа
            var inferred = false;
            var key = cb.Key;
            if (key is null && cb.OptionsType is { } ot && cb.Prop is { } prop && SectionOf(ot, sections, out inferred) is { } sec) key = $"{sec}:{prop}";
            if (key is null) return;
            var cut = url.IndexOfAny(['?', '#']);
            if (cut >= 0) url = url[..cut];                       // query и fragment (токены) в Url/Evidence не попадают — как у абсолютных адресов
            var note = (how is null ? "" : $"{how}; ") + $"адрес из конфигурации {key}" + (inferred ? "; секция выведена из имени класса" : "");
            hits.Add(new HttpCallHit(verb, url, path, line, $"{verb} {url} ({note})", ViaBase: true, ConfigKey: key, AppendToBasePath: append));
            return;
        }
        var target = append
            ? $"{baseUri.GetLeftPart(UriPartial.Authority)}{baseUri.AbsolutePath.TrimEnd('/')}/{url.TrimStart('/')}"   // Refit: путь дописывается к пути базы
            : null;
        Uri? uri = null;
        if (!(target is null ? Uri.TryCreate(baseUri, url, out uri) : Uri.TryCreate(target, UriKind.Absolute, out uri))) return;
        // после слияния цель обязана остаться на хосте базового адреса (защита от хитрых относительных строк)
        if (!uri.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase) || uri.Scheme != baseUri.Scheme) return;
        var clean = $"{uri.Scheme}://{uri.Authority}{UnescapeBraces(uri.AbsolutePath)}";
        hits.Add(new HttpCallHit(verb, clean, path, line, $"{verb} {clean} ({how ?? "по BaseAddress"})", ViaBase: true, AppendToBasePath: append));
    }
}
