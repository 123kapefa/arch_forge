using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Analysis.Infrastructure.Roslyn;
using Microsoft.Extensions.Logging;

namespace ArchForge.Analysis.Tests.Systems;

// Пометка «семантика не получилась → синтаксический пол» (adr/0008): честно в обе стороны, без ложных срабатываний.
public class SemanticFloorTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "af_floor_" + Guid.NewGuid().ToString("N"), "a")).Parent!.FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ } }

    private sealed class OneSvc : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) =>
            [new DiscoveredService("Svc.A", Path.Combine(root, "a"), "docker-compose.yml")];
    }

    private sealed class Types(params RawType[] t) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => t;
    }

    private static readonly RawType Foo = new("Foo", "class", [], "", "Domain", "Foo.cs");

    private sealed class Scanned(bool syntaxOnly, params RawType[] t) : IScannedTypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => t;
        public TypeScan ReadScan(string path, CancellationToken ct = default) => new(t, syntaxOnly);
    }

    private ServiceAnalysis Run(ITypeSource types) =>
        Assert.Single(new ServiceAnalyzer(new OneSvc(), types, new TypeClassifier()).Analyze(_dir, ConventionPack.Default()));

    [Fact] public void Floor_result_is_marked() => Assert.True(Run(new Scanned(true, Foo)).SyntaxOnly);
    [Fact] public void Semantic_result_is_not_marked() => Assert.False(Run(new Scanned(false, Foo)).SyntaxOnly);
    [Fact] public void Plain_type_source_is_not_marked() => Assert.False(Run(new Types(Foo)).SyntaxOnly);
    [Fact] public void Service_without_types_is_not_marked() => Assert.False(Run(new Scanned(true)).SyntaxOnly);

    // Подмена семантики: пустой результат или сбой → настоящий путь Read → degraded → TypeScan → кеш → пометка.
    private sealed class FakeSemantic(bool throws, ILogger<RoslynSemanticTypeSource>? log = null) : RoslynSemanticTypeSource(new RoslynTypeSource(), log)
    {
        internal override IReadOnlyList<RawType>? ReadSemantic(string path, CancellationToken ct) =>
            throws ? throw new InvalidOperationException("roslyn boom") : [];
    }

    [Theory]
    [InlineData(false)]   // пустая семантика
    [InlineData(true)]    // исключение
    public void Real_chain_semantic_failure_falls_to_floor_and_is_marked(bool throws)
    {
        File.WriteAllText(Path.Combine(_dir, "a", "Foo.cs"), "namespace X; public class Foo { }");
        var cache = new ScanCache();
        var types = new CachingTypeSource(new FakeSemantic(throws), cache);
        var a = Run(types);
        Assert.True(a.SyntaxOnly);
        Assert.NotEmpty(a.Result!.Types);                                 // пол реально отдал типы
        Assert.True(types.ReadScan(Path.Combine(_dir, "a")).SyntaxOnly);  // из кеша тот же вердикт
    }

    // Боевой путь: настоящая семантика пола не даёт → пометки нет, ни без bin/, ни с реальным bin/ и DLL.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_semantics_is_not_marked_floor(bool withBin)
    {
        File.WriteAllText(Path.Combine(_dir, "a", "Foo.cs"), "namespace X; public class Foo { }");
        if (withBin)
        {
            var bin = Directory.CreateDirectory(Path.Combine(_dir, "a", "bin", "Debug")).FullName;
            var dll = typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.Location;
            File.Copy(dll, Path.Combine(bin, Path.GetFileName(dll)));
        }
        var a = Run(new CachingTypeSource(new RoslynSemanticTypeSource(new RoslynTypeSource()), new ScanCache()));
        Assert.False(a.SyntaxOnly);
        Assert.NotEmpty(a.Result!.Types);
    }

    // Ответ /api/system: строка про пол доходит до degradations, у здорового сервиса её нет.
    private sealed class FakeSource(SystemGraph g) : ISystemSource
    {
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default) => g;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Response_degradations_contain_floor_note(bool floor)
    {
        var handler = new GetSystemHandler(
            new FakeSource(new SystemGraph([new SystemNode("a/a-api", "a-api", SystemNodeKind.Service)], [], [])),
            new ServiceAnalyzer(new OneSvc(), new Scanned(floor, Foo), new TypeClassifier()));
        var r = handler.Handle(new GetSystemQuery(_dir, Layers: true))!;
        var notes = (r.Degradations ?? []).Where(d => d.Contains("семантика не получилась")).ToList();
        if (floor)
        {
            var n = Assert.Single(notes);
            Assert.Contains("сервис «Svc.A»", n);
            Assert.Contains("пустой результат", n);
            Assert.Contains("типы по синтаксису", n);
        }
        else Assert.Empty(notes);
    }
}
