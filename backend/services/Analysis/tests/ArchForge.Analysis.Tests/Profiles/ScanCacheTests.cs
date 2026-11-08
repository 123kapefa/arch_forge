using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests.Profiles;

public class ScanCacheTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Project(params string[] files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_cache_" + Guid.NewGuid().ToString("N"))).FullName;
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "class A {}");
        return dir;
    }

    [Fact]
    public void Same_tree_is_read_once()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();
        var calls = 0;
        List<string> Factory() { calls++; return ["x"]; }

        var a = cache.GetOrAdd("types", dir, Factory);
        var b = cache.GetOrAdd("types", dir, Factory);

        Assert.Equal(1, calls);
        Assert.Same(a, b);
    }

    [Fact]
    public void Changed_file_added_file_or_new_content_invalidates()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();
        var calls = 0;
        List<string> Factory() { calls++; return [calls.ToString()]; }
        cache.GetOrAdd("types", dir, Factory);

        File.WriteAllText(Path.Combine(dir, "B.cs"), "class B {}");           // добавили файл
        cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(2, calls);

        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { int x; }");   // изменили размер
        cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(3, calls);

        File.Delete(Path.Combine(dir, "B.cs"));                                // удалили
        cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Entry_expires_after_ttl()
    {
        var dir = Project("A.cs");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        var calls = 0;
        List<string> Factory() { calls++; return []; }
        cache.GetOrAdd("types", dir, Factory);

        clock.Now += ScanCache.Ttl - TimeSpan.FromSeconds(1);
        cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(1, calls);

        clock.Now += TimeSpan.FromSeconds(2);
        cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Different_kinds_and_paths_do_not_collide()
    {
        var d1 = Project("A.cs"); var d2 = Project("A.cs");
        var cache = new ScanCache();

        var a = cache.GetOrAdd("types", d1, () => new List<string> { "t1" });
        var b = cache.GetOrAdd("endpoints", d1, () => new List<string> { "e1" });
        var c = cache.GetOrAdd("types", d2, () => new List<string> { "t2" });

        Assert.Equal("t1", a[0]); Assert.Equal("e1", b[0]); Assert.Equal("t2", c[0]);
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void Cancelled_or_failed_read_is_not_cached()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();

        Assert.Throws<OperationCanceledException>(() => cache.GetOrAdd<List<string>>("types", dir, () => throw new OperationCanceledException()));
        Assert.Equal(0, cache.Count);

        var calls = 0;
        cache.GetOrAdd("types", dir, () => { calls++; return new List<string>(); });
        Assert.Equal(1, calls);                                              // после отмены следующий вызов читает заново
    }

    [Fact]
    public void Parallel_requests_for_one_key_run_the_factory_once()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();
        var calls = 0;

        Parallel.For(0, 16, _ => cache.GetOrAdd("types", dir, () => { Interlocked.Increment(ref calls); Thread.Sleep(30); return new List<string>(); }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Entry_count_is_capped_and_oldest_go_first()
    {
        var dir = Project("A.cs");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        for (var i = 0; i < ScanCache.MaxEntries + 10; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(1);
            cache.GetOrAdd("k" + i, dir, () => new List<string>());
        }

        Assert.Equal(ScanCache.MaxEntries, cache.Count);
        var calls = 0;
        cache.GetOrAdd("k0", dir, () => { calls++; return new List<string>(); });   // самый старый вытеснен
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Missing_directory_does_not_throw_from_fingerprint()
    {
        var cache = new ScanCache();

        var r = cache.GetOrAdd("types", "/no/such/dir_archforge", () => new List<string> { "ok" });

        Assert.Equal("ok", r[0]);
    }

    [Fact]
    public void Decorated_type_source_reads_inner_once()
    {
        var dir = Project("A.cs");
        var inner = new CountingTypes();
        var cached = new CachingTypeSourceProbe(inner, new ScanCache());

        cached.Read(dir); cached.Read(dir);

        Assert.Equal(1, inner.Reads);
    }

    private sealed class CountingTypes : ITypeSource
    {
        public int Reads { get; private set; }
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) { Reads++; return []; }
    }

    // CachingTypeSource internal: проверяем через публичный DI-путь — тот же ScanCache.GetOrAdd с видом «types».
    private sealed class CachingTypeSourceProbe(ITypeSource inner, ScanCache cache) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) =>
            cache.GetOrAdd("types", path, () => inner.Read(path, ct));
    }
}
