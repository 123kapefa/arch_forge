using System.Collections.Concurrent;
using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

/// <summary>Кеш результатов дорогого разбора Roslyn по пути (adr/0012, предпросмотр влияния): повторный разбор того же кода —
/// только детекторы и классификация, без чтения исходников.
/// Запись живёт, пока не изменился «отпечаток» дерева (хеш по путям, времени изменения и размерам всех .cs, а также
/// .csproj и Directory.*.props) и не вышел срок. Деградированный результат (семантика откатилась на синтаксический пол)
/// живёт коротко: восстановление пакетов или починка сборки подхватятся быстро. Число записей ограничено; просроченные
/// и вытесненные уходят вместе со своими замками. Отмена и ошибка ничего не кешируют. Параллельные запросы одного ключа
/// ждут первого, и ожидание можно отменить. «Пере-анализировать» сбрасывает записи принудительно (<see cref="InvalidateUnder"/>).</summary>
public sealed class ScanCache(TimeProvider? time = null) : IScanCacheControl
{
    internal const int MaxEntries = 256;
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DegradedTtl = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FingerprintMemo = TimeSpan.FromSeconds(2);   // отпечаток одного пути не пересчитываем чаще

    private sealed record Entry(string Fingerprint, DateTimeOffset At, TimeSpan Ttl, object Value);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Fp, DateTimeOffset At)> _fpMemo = new(StringComparer.Ordinal);

    public int Count => _map.Count;
    internal int LockCount => _locks.Count;

    /// <summary>Результат без признака деградации (эндпоинты, вызовы http, конструирование).</summary>
    public T GetOrAdd<T>(string kind, string path, Func<T> factory, CancellationToken ct = default) where T : class =>
        GetOrAddDegradable(kind, path, () => (factory(), false), ct);

    /// <summary>Как <see cref="GetOrAdd{T}"/>, но фабрика сообщает, что результат деградировал: он кешируется на короткий срок.</summary>
    public T GetOrAddDegradable<T>(string kind, string path, Func<(T Value, bool Degraded)> factory, CancellationToken ct = default) where T : class
    {
        path = NormalizeKey(path);                       // одно место на диске — один ключ: «/a/b», «/a/b/», «/A/B» (macOS/Windows) не плодят копии
        var key = $"{kind}|{path}";
        var fp = Fingerprint(path);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        gate.Wait(ct);                                   // отмена прерывает ожидание, а не держит поток пула
        try
        {
            var now = _time.GetUtcNow();
            if (_map.TryGetValue(key, out var e) && e.Fingerprint == fp && now - e.At < e.Ttl && e.Value is T hit) return hit;
            var (value, degraded) = factory();           // отмена/ошибка: исключение уходит, запись не создаётся
            _map[key] = new Entry(fp, now, degraded ? DegradedTtl : Ttl, value);
            Evict(now);
            return value;
        }
        finally { gate.Release(); }
    }

    /// <summary>Сбрасывает записи для всех путей внутри <paramref name="root"/> — «Пере-анализировать».</summary>
    public void InvalidateUnder(string root)
    {
        var r = NormalizeKey(root);
        foreach (var k in _map.Keys.Where(k => Inside(r, k[(k.IndexOf('|') + 1)..])).ToList()) _map.TryRemove(k, out _);
        foreach (var k in _fpMemo.Keys.Where(k => Inside(r, k)).ToList()) _fpMemo.TryRemove(k, out _);
    }

    /// <summary>Ключ пути: полный путь без хвостового разделителя; на Windows и macOS (ФС без учёта регистра) — в нижнем регистре.
    /// Симлинки не разворачиваются: в разборе путь используется как задан.</summary>
    internal static string NormalizeKey(string p)
    {
        string full;
        try { full = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { full = p; }                              // недопустимый путь — ключ как есть, разбор сам вернёт ошибку
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
    }

    private static bool Inside(string root, string path)
    {
        var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.Equals(root, StringComparison.Ordinal) || p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Просроченные уходят первыми, затем самые старые сверх лимита; замок убираем вместе с записью, если он свободен.
    private void Evict(DateTimeOffset now)
    {
        foreach (var kv in _map.Where(kv => now - kv.Value.At >= kv.Value.Ttl).ToList()) Drop(kv.Key);
        if (_map.Count > MaxEntries)
            foreach (var k in _map.OrderBy(kv => kv.Value.At).Take(_map.Count - MaxEntries).Select(kv => kv.Key).ToList()) Drop(k);
        // замки без записи (после отмены/ошибки) тоже не копим
        foreach (var k in _locks.Keys.Where(k => !_map.ContainsKey(k)).ToList())
            if (_locks.TryGetValue(k, out var s) && s.CurrentCount == 1 && _locks.TryRemove(k, out var removed) && removed.CurrentCount != 1)
                _locks.TryAdd(k, removed);              // кто-то успел взять замок — возвращаем
    }

    private void Drop(string key) => _map.TryRemove(key, out _);

    /// <summary>Отпечаток дерева: хеш по (путь, время изменения, размер) всех .cs, а также .csproj и Directory.*.props.
    /// Хеш, а не «максимум»: замена файла другим того же размера или откат времени тоже меняют отпечаток.</summary>
    private string Fingerprint(string path)
    {
        var now = _time.GetUtcNow();
        if (_fpMemo.TryGetValue(path, out var m) && now - m.At < FingerprintMemo) return m.Fp;
        var fp = ComputeFingerprint(path);
        _fpMemo[path] = (fp, now);
        return fp;
    }

    internal static string ComputeFingerprint(string path)
    {
        long count = 0, acc = 0;
        void Add(string f)
        {
            try
            {
                var fi = new FileInfo(f);
                count++;
                acc = unchecked(acc + HashCode.Combine(f, fi.LastWriteTimeUtc.Ticks, fi.Length));
            }
            catch { /* недоступный файл не должен ронять отпечаток */ }
        }
        try
        {
            foreach (var f in RoslynScan.EnumerateCsCapped(path, out _)) Add(f);   // тот же кап, что и у разбора (20 000 .cs)
            foreach (var f in Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories).Where(f => !PathFilter.IsNoise(path, f))) Add(f);
            foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json" })
            {
                var f = Path.Combine(path, name);
                if (File.Exists(f)) Add(f);
            }
        }
        catch { /* нет папки — отпечаток пустой, разбор сам вернёт ошибку */ }
        return $"{count}:{acc}";
    }
}

// Декораторы портов: тот же контракт, результат берётся из кеша. Ключ — вид источника + путь сервиса.
internal sealed class CachingTypeSource(RoslynSemanticTypeSource inner, ScanCache cache) : ITypeSource
{
    public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) =>
        cache.GetOrAddDegradable<IReadOnlyList<RawType>>("types", path, () =>
        {
            var r = inner.Read(path, out var degraded, ct);   // деградация (пол вместо семантики) — короткий срок жизни записи
            return (r, degraded);
        }, ct);
}

internal sealed class CachingEndpointSource(IEndpointSource inner, ScanCache cache) : IEndpointSource
{
    public IReadOnlyList<EndpointHit> Read(string root, CancellationToken ct = default) =>
        cache.GetOrAdd("endpoints", root, () => inner.Read(root, ct), ct);
}

internal sealed class CachingHttpCallSource(IHttpCallSource inner, ScanCache cache) : IHttpCallSource
{
    public IReadOnlyList<HttpCallHit> Read(string root, CancellationToken ct = default) =>
        cache.GetOrAdd("httpcalls", root, () => inner.Read(root, ct), ct);
}

internal sealed class CachingConstructionSource(IConstructionSource inner, ScanCache cache) : IConstructionSource
{
    public IReadOnlyList<ConstructionSite> Read(string path, CancellationToken ct = default) =>
        cache.GetOrAdd("construction", path, () => inner.Read(path, ct), ct);
}
