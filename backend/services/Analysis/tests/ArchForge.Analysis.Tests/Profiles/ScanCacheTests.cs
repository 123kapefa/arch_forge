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
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        var calls = 0;
        List<string> Factory() { calls++; return [calls.ToString()]; }
        void Tick() => clock.Now += ScanCache.FingerprintMemo + TimeSpan.FromMilliseconds(1);   // отпечаток пересчитывается не чаще раза в 2 с
        cache.GetOrAdd("types", dir, Factory);

        File.WriteAllText(Path.Combine(dir, "B.cs"), "class B {}");           // добавили файл
        Tick(); cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(2, calls);

        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { int x; }");   // изменили размер
        Tick(); cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(3, calls);

        File.Delete(Path.Combine(dir, "B.cs"));                                // удалили
        Tick(); cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Csproj_and_props_changes_invalidate()
    {
        var dir = Project("A.cs");
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project />");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        var calls = 0;
        List<string> Factory() { calls++; return []; }
        void Tick() => clock.Now += ScanCache.FingerprintMemo + TimeSpan.FromMilliseconds(1);
        cache.GetOrAdd("types", dir, Factory);

        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project><ItemGroup /></Project>");
        Tick(); cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(2, calls);

        File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"), "<Project />");
        Tick(); cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void File_replaced_by_same_size_with_older_time_is_still_detected()
    {
        var dir = Project("A.cs");
        var f = Path.Combine(dir, "A.cs");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        var calls = 0;
        List<string> Factory() { calls++; return []; }
        cache.GetOrAdd("types", dir, Factory);

        File.WriteAllText(f, "class B {}");                                    // тот же размер, другое содержимое
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-30));             // и время откатили назад (rsync -t, бэкап)
        clock.Now += ScanCache.FingerprintMemo + TimeSpan.FromMilliseconds(1);
        cache.GetOrAdd("types", dir, Factory);

        Assert.Equal(2, calls);                                                // отпечаток — хеш по времени каждого файла, а не максимум
    }

    [Fact]
    public void Edits_inside_the_memo_window_are_seen_only_after_it_or_after_refresh()
    {
        var dir = Project("A.cs");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        var calls = 0;
        List<string> Factory() { calls++; return []; }
        cache.GetOrAdd("types", dir, Factory);

        File.WriteAllText(Path.Combine(dir, "B.cs"), "class B {}");
        cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(1, calls);                                                // окно 2 с: отпечаток не пересчитывался

        cache.InvalidateUnder(dir);                                            // «Пере-анализировать»
        cache.GetOrAdd("types", dir, Factory);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Degraded_result_is_cached_only_briefly()
    {
        var dir = Project("A.cs");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        var calls = 0;
        (List<string>, bool) Degraded() { calls++; return ([], true); }
        cache.GetOrAddDegradable("types", dir, Degraded);

        clock.Now += ScanCache.DegradedTtl - TimeSpan.FromSeconds(1);
        cache.GetOrAddDegradable("types", dir, Degraded);
        Assert.Equal(1, calls);                                                // в пределах короткого срока — из кеша (правки профиля быстрые)

        clock.Now += TimeSpan.FromSeconds(2);                                  // дольше 30 с — читаем заново (восстановили пакеты, починили сборку)
        cache.GetOrAddDegradable("types", dir, Degraded);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Healthy_result_outlives_the_degraded_ttl()
    {
        var dir = Project("A.cs");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        var calls = 0;
        (List<string>, bool) Healthy() { calls++; return ([], false); }
        cache.GetOrAddDegradable("types", dir, Healthy);

        clock.Now += ScanCache.DegradedTtl + TimeSpan.FromSeconds(5);
        cache.GetOrAddDegradable("types", dir, Healthy);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void InvalidateUnder_clears_nested_paths_but_not_siblings_with_a_common_prefix()
    {
        var root = Project("A.cs");
        var inner = Directory.CreateDirectory(Path.Combine(root, "svc")).FullName;
        File.WriteAllText(Path.Combine(inner, "S.cs"), "class S {}");
        var sibling = root + "x";                                               // "…/proj" и "…/projx": общий префикс, разные папки
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "X.cs"), "class X {}");
        var cache = new ScanCache();
        cache.GetOrAdd("types", root, () => new List<string>());
        cache.GetOrAdd("types", inner, () => new List<string>());
        cache.GetOrAdd("types", sibling, () => new List<string>());

        cache.InvalidateUnder(root);

        Assert.Equal(1, cache.Count);                                           // остался только соседний проект
    }

    [Fact]
    public void Waiting_for_another_read_can_be_cancelled()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var slow = Task.Run(() => cache.GetOrAdd("types", dir, () => { started.Set(); release.Wait(); return new List<string>(); }));
        started.Wait();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => cache.GetOrAdd("types", dir, () => new List<string>(), cts.Token));

        Assert.True(sw.ElapsedMilliseconds < 2000);                             // ожидание у замка прервано отменой, а не висит до конца чужого чтения
        release.Set();
        slow.Wait();
    }

    [Fact]
    public void Expired_entries_and_their_locks_are_purged()
    {
        var dir = Project("A.cs");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new ScanCache(clock);
        for (var i = 0; i < 20; i++) cache.GetOrAdd("k" + i, dir, () => new List<string>());
        Assert.Equal(20, cache.Count);

        clock.Now += ScanCache.Ttl + TimeSpan.FromSeconds(1);
        cache.GetOrAdd("fresh", dir, () => new List<string>());                 // следующая запись вычищает просроченные

        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.LockCount);                                       // замки просроченных ключей не копятся
    }

    [Fact]
    public void Failed_reads_leave_no_locks_behind_after_the_next_write()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();
        for (var i = 0; i < 10; i++)
            Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd<List<string>>("bad" + i, dir, () => throw new InvalidOperationException()));

        cache.GetOrAdd("ok", dir, () => new List<string>());

        Assert.Equal(1, cache.LockCount);
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

    [Fact]
    public void Different_spellings_of_one_path_share_a_single_entry()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();
        var calls = 0;
        List<string> Factory() { calls++; return []; }

        cache.GetOrAdd("types", dir, Factory);
        cache.GetOrAdd("types", dir + Path.DirectorySeparatorChar, Factory);            // хвостовой разделитель
        cache.GetOrAdd("types", Path.Combine(dir, "..", Path.GetFileName(dir)), Factory); // «..» в пути
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
            cache.GetOrAdd("types", dir.ToUpperInvariant(), Factory);                    // ФС без учёта регистра

        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.LockCount);
    }

    [Fact]
    public void InvalidateUnder_works_with_a_differently_spelled_root()
    {
        var dir = Project("A.cs");
        var cache = new ScanCache();
        cache.GetOrAdd("types", dir, () => new List<string>());

        cache.InvalidateUnder(dir + Path.DirectorySeparatorChar);

        Assert.Equal(0, cache.Count);
    }
}

