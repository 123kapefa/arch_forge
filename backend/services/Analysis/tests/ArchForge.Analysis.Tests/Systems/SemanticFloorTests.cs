using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests.Systems;

// Пометка «семантика не получилась → синтаксический пол» (adr/0008): честно в обе стороны, без ложных срабатываний.
public class SemanticFloorTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "af_floor_" + Guid.NewGuid().ToString("N"))).FullName;
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

    private sealed class Status(bool syntaxOnly, bool throws = false) : ISemanticStatusSource
    {
        public bool IsSyntaxOnly(string root) => throws ? throw new InvalidOperationException() : syntaxOnly;
    }

    private static readonly RawType Foo = new("Foo", "class", [], "", "Domain", "Foo.cs");

    private ServiceAnalysis Run(ITypeSource types, ISemanticStatusSource? status) =>
        Assert.Single(new ServiceAnalyzer(new OneSvc(), types, new TypeClassifier(), semanticStatus: status).Analyze(_dir, ConventionPack.Default()));

    [Fact] public void Floor_result_is_marked() => Assert.True(Run(new Types(Foo), new Status(true)).SyntaxOnly);
    [Fact] public void Semantic_result_is_not_marked() => Assert.False(Run(new Types(Foo), new Status(false)).SyntaxOnly);
    [Fact] public void No_status_source_is_not_marked() => Assert.False(Run(new Types(Foo), null).SyntaxOnly);
    [Fact] public void Status_failure_does_not_break_analysis_or_mark() => Assert.False(Run(new Types(Foo), new Status(true, throws: true)).SyntaxOnly);
    [Fact] public void Service_without_types_is_not_marked() => Assert.False(Run(new Types(), new Status(true)).SyntaxOnly);

    // кеш: флаг ставит тот, кто читал типы; нет записи → «не знаем» = не метим
    [Fact]
    public void Cache_status_reflects_degraded_entry_only()
    {
        var cache = new ScanCache();
        var status = new CacheSemanticStatusSource(cache);
        Assert.False(status.IsSyntaxOnly(_dir));                                        // записи нет
        cache.GetOrAddDegradable<IReadOnlyList<RawType>>("types", _dir, () => ([Foo], true));
        Assert.True(status.IsSyntaxOnly(_dir));
        cache.InvalidateUnder(_dir);
        cache.GetOrAddDegradable<IReadOnlyList<RawType>>("types", _dir, () => ([Foo], false));
        Assert.False(status.IsSyntaxOnly(_dir));
    }

    // Боевой путь: настоящая семантика (с bin/ и без него — рантайм-ссылки есть) пола не даёт → пометки нет.
    [Fact]
    public void Real_semantics_without_bin_is_not_marked_floor()
    {
        File.WriteAllText(Path.Combine(_dir, "Foo.cs"), "namespace X; public class Foo { }");
        var cache = new ScanCache();
        var types = new CachingTypeSource(new RoslynSemanticTypeSource(new RoslynTypeSource()), cache);
        Assert.NotEmpty(types.Read(_dir));
        Assert.False(new CacheSemanticStatusSource(cache).IsSyntaxOnly(_dir));
    }
}
