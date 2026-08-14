using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Общие хелперы разбора для синтаксического и семантического источников.
internal static class RoslynScan
{
    // Хэндвритен .cs редко >2 МБ; крупнее — почти всегда генерённое/данные (парсить дорого, толку нет).
    private const long MaxFileBytes = 2_000_000;

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
        if (p.Contains("Test")) return "tests";
        if (p.Contains(".Domain")) return "Domain";
        if (p.Contains(".Application") || p.Contains(".Core")) return "Application";
        if (p.Contains(".Infrastructure") || p.Contains(".Persistence")) return "Infrastructure";
        if (p.Contains(".Contracts")) return "Contracts";
        if (p.Contains(".Migrator")) return "Tooling";
        if (p.Contains(".Api") || p.Contains(".Web") || p.Contains(".Host")) return "Api";
        return "unknown";
    }
}
