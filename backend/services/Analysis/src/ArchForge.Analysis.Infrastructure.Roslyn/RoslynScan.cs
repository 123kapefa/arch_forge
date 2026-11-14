using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Общие хелперы разбора для синтаксического и семантического источников.
internal static class RoslynScan
{
    // Хэндвритен .cs редко >2 МБ; крупнее — почти всегда генерённое/данные (парсить дорого, толку нет).
    private const long MaxFileBytes = 2_000_000;

    // Кап числа .cs на один разбор: пата-репо (десятки тысяч файлов) душат парсер/память. Backstop против
    // аномалий; реальные сервисы (сотни–тысячи файлов) не задевает. Урезание видимо в логах вызывающего (adr/0008).
    internal const int MaxFilesPerScan = 20_000;

    // .cs таргета, кроме генерённого мусора (.g.cs/.Designer.cs), шумовых каталогов (bin/obj, копии/дампы,
    // скрытые .git/.claude/worktrees — единый PathFilter) и гигантских файлов (adr/0008).
    public static IEnumerable<string> EnumerateCs(string path) =>
        SafeFiles.Enumerate(path, "*.cs")
            .Where(f =>
            {
                if (PathFilter.IsNoise(path, f)) return false; // копии из .claude/worktrees и т.п. дублируют разбор
                var n = f.Replace('\\', '/');
                if (n.EndsWith(".g.cs") || n.EndsWith(".Designer.cs")) return false;
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

    // Потолок вложенности скобок/дженериков. Переполнение стека не ловится try/catch: Task<Task<…>> на ~1000 уровнях (стек 1,5 МБ пула)
    // роняет ВЕСЬ процесс Analysis. Замеры: угловые падают между 1000 и 2000, круглые между 4000 и 8000, вложенная интерполяция
    // между 1000 и 2000. 200 даёт запас ≥5×, а настоящий код глубже ~30 не бывает. Выше — файл пропускается как деградация (#167).
    internal const int MaxNestingDepth = 200;

    // Парсер и биндер Roslyn рекурсивны, а StackOverflow в .NET не ловится try/catch и роняет ВЕСЬ процесс. Текстовый предфильтр
    // (NestingDepth) ловит скобки/дженерики, но не цепочки `?.b?.b`, `not not`, вложенный XML в ///, `A.B.C…` и обходится
    // комментариями, поэтому вся работа с Roslyn идёт на потоке с большим стеком. 256 МБ — виртуальная резервация, память
    // коммитится по мере роста; файл ограничен MaxFileBytes (2 МБ), так что худший замеренный случай укладывается с запасом.
    internal const int DeepStackBytes = 256 * 1024 * 1024;

    [ThreadStatic] private static bool _onBigStack;

    /// <summary>Выполняет <paramref name="work"/> на выделенном потоке с большим стеком (вложенный вызов — на том же потоке).
    /// Исключения (в том числе отмена) пробрасываются как есть.</summary>
    internal static T OnBigStack<T>(Func<T> work)
    {
        if (_onBigStack) return work();
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            _onBigStack = true;
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }, DeepStackBytes) { IsBackground = true, Name = "roslyn-deep-stack" };
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result!;
    }

    // Куда писать пропуск файла: источники создаются без DI, поэтому лог общий; Registration подставляет настоящий.
    internal static ILogger Log { get; set; } = NullLogger.Instance;

    /// <summary>Максимальная глубина вложенности <c>( [ { &lt;</c> по тексту, без разбора. Дёшево (один проход по символам)
    /// и детерминированно. Считает и то, что внутри строк и концевых комментариев: это завышает, но не занижает (несбалансированные скобки в них копятся на весь файл — известное ограничение, на порог 200 нужно ≥200 таких вхождений) (вложенная
    /// интерполяция <c>$"{$"{…</c> тоже должна ловиться). Угловая скобка счётчика <c>&lt;</c> сбрасывается на <c>; { }</c>
    /// (дженерик через них не тянется), чтобы операторы сравнения в длинном файле не копили глубину.</summary>
    internal static int NestingDepth(string text)
    {
        int depth = 0, angle = 0, max = 0;
        var lineStart = true;                     // до первого непробельного символа строки
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') { lineStart = true; continue; }
            if (lineStart)
            {
                if (char.IsWhiteSpace(c)) continue;
                lineStart = false;
                // Строка, начинающаяся с «//», — комментарий, а в строковом литерале (многострочном) это не код: пропуск
                // безопасен в обоих случаях и не даёт «:(» в комментариях копить глубину. Концевые комментарии и «/* */»
                // не пропускаем: «//» бывает внутри строки, а пропуск кода занизил бы счётчик.
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    var nl = text.IndexOf('\n', i);
                    if (nl < 0) break;
                    i = nl - 1;
                    continue;
                }
            }
            switch (c)
            {
                case '(' or '[': depth++; break;
                case '{': depth++; angle = 0; break;
                case ')' or ']': if (depth > 0) depth--; break;
                case '}': if (depth > 0) depth--; angle = 0; break;
                case ';': angle = 0; break;
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
            }
            if (depth + angle > max) max = depth + angle;
        }
        return max;
    }

    /// <summary>Единственное место, где источники зовут <c>ParseText</c>: слишком глубокий файл не разбираем (null) и
    /// пишем деградацию в лог. Вызывающий пропускает файл (<c>continue</c>), остальные файлы разбираются как обычно.</summary>
    internal static SyntaxTree? TryParse(string text, string file, CancellationToken ct = default)
    {
        var depth = NestingDepth(text);
        if (depth > MaxNestingDepth)
        {
            Log.LogWarning("Файл пропущен: вложенность скобок {Depth} > {Max} (защита от переполнения стека): {File}", depth, MaxNestingDepth, file.ReplaceLineEndings(" "));   // имя файла бывает с переводом строки — не даём подделать строку лога
            return null;
        }
        return CSharpSyntaxTree.ParseText(text, path: file, cancellationToken: ct);
    }

    /// <summary>Корень дерева или null, если файл слишком глубок (см. <see cref="TryParse"/>).</summary>
    internal static SyntaxNode? TryParseRoot(string text, string file, CancellationToken ct = default) =>
        TryParse(text, file, ct)?.GetRoot(ct);

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

    /// <summary>Слой файла по пути ОТНОСИТЕЛЬНО корня сканирования (с последним сегментом корня).
    /// Имена папок выше корня («/Users/me/archforge-testbed/…», «/home/ci/latest/…») слоя определять не должны:
    /// раньше «Test» в абсолютном пути превращал ВЕСЬ код в слой tests, и анализ проекта в папке «…testbed» был пуст.</summary>
    public static string Layer(string file, string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('/', '\\');
        var fileFull = Path.GetFullPath(file);
        var anchor = Path.GetDirectoryName(rootFull);                          // родитель корня: сам корень остаётся в пути
        // файловые системы macOS/Windows не различают регистр: корень, переданный в другом регистре, не должен тихо откатывать на старое поведение
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.IsNullOrEmpty(anchor) || !fileFull.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp))
            return Layer(file);                                                // файл вне корня (не должно быть) — как раньше
        return Layer(Path.GetRelativePath(anchor, fileFull));
    }

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
