using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Общие хелперы разбора для синтаксического и семантического источников.
internal static class RoslynScan
{
    // Хэндвритен .cs редко >2 МБ; крупнее — почти всегда генерённое/данные (парсить дорого, толку нет).
    private const long MaxFileBytes = 2_000_000;

    // Кап числа .cs на один разбор: пата-репо (десятки тысяч файлов) душат парсер/память. Backstop против
    // аномалий; реальные сервисы (сотни–тысячи файлов) не задевает. Урезание видимо в логах вызывающего (adr/0008).
    internal const int MaxFilesPerScan = 20_000;

    // .cs таргета, кроме генерённого/сборочного мусора (adr/0008) и гигантских файлов (робастность).
    public static IEnumerable<string> EnumerateCs(string path) =>
        Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var n = f.Replace('\\', '/');
                if (n.Contains("/obj/") || n.Contains("/bin/")
                    || n.EndsWith(".g.cs") || n.EndsWith(".Designer.cs")) return false;
                try { return new FileInfo(f).Length <= MaxFileBytes; }
                catch { return false; } // недоступный файл — пропускаем (изоляция ошибок)
            });

    /// <summary>Список .cs с капом числа файлов (backstop). <paramref name="capped"/> = список был урезан
    /// (пата-репо) → вызывающий логирует деградацию (adr/0008).</summary>
    internal static IReadOnlyList<string> EnumerateCsCapped(string path, out bool capped)
    {
        var files = EnumerateCs(path).ToList();
        capped = files.Count > MaxFilesPerScan;
        if (!capped) return files;
        files.Sort(StringComparer.Ordinal); // порядок EnumerateFiles не гарантирован → сортируем, чтобы усечение было детерминированным
        return files.Take(MaxFilesPerScan).ToList();
    }

    public static bool IsTypeName(string name) =>
        name.Length > 0 && (char.IsUpper(name[0]) || name[0] == '_');

    // Простое имя типа (без дженериков/квалификаторов/nullable) — для матчинга с паком по имени.
    public static string Simple(TypeSyntax t) => t switch
    {
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        QualifiedNameSyntax q => Simple(q.Right),
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        NullableTypeSyntax n => Simple(n.ElementType),
        _ => t.ToString(),
    };

    public static IReadOnlyList<string> SyntacticBases(TypeDeclarationSyntax decl) =>
        decl.BaseList?.Types.Select(b => Simple(b.Type)).ToList() ?? [];

    public static string Layer(string path)
    {
        var p = path.Replace('\\', '/');
        // Сегменты проектов сравниваем без учёта регистра: «Catalog.API» ≡ «.Api» (иначе весь API-проект → unknown).
        bool Has(string s) => p.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Test")) return "tests";
        // общий kernel (Kalmykov.Shared.*: SharedKernel/Core/Framework/Authentication/…) — отдельный сквозной
        // слой, а не «unknown» и не слой конкретного сервиса. Ловим по dotted-сегменту, чтобы не задеть
        // внутрисервисные папки вида Catalog.Application/Shared. Раньше маркеров слоя (Shared.Contracts → Shared).
        if (Has(".Shared.")) return "Shared";
        if (Has(".Domain")) return "Domain";
        if (Has(".Application") || Has(".Core")) return "Application";
        if (Has(".Infrastructure") || Has(".Persistence")) return "Infrastructure";
        if (Has(".Contracts")) return "Contracts";
        if (Has(".Migrator")) return "Tooling";
        if (Has(".Api") || Has(".Web") || Has(".Host")) return "Api";
        return "unknown";
    }
}
