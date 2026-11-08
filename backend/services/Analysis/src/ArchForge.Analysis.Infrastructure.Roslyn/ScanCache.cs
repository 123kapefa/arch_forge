using System.Collections.Concurrent;
using ArchForge.Analysis.Core;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

/// <summary>Кеш результатов дорогого разбора Roslyn по пути (adr/0012, предпросмотр влияния): повторный разбор
/// того же кода — только детекторы и классификация, без чтения исходников. Запись живёт, пока не изменился «отпечаток»
/// дерева (число .cs, самое позднее изменение, суммарный размер) и не вышел TTL; число записей ограничено.
/// Отмена посреди разбора ничего не кеширует (исключение уходит наверх). Параллельные запросы одного ключа ждут первого.
/// Ограничение: изменения в .csproj/NuGet отпечаток не видит — их подхватит TTL.</summary>
public sealed class ScanCache(TimeProvider? time = null)
{
    internal const int MaxEntries = 256;
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private sealed record Entry(string Fingerprint, DateTimeOffset At, object Value);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    public int Count => _map.Count;

    public T GetOrAdd<T>(string kind, string path, Func<T> factory) where T : class
    {
        var key = $"{kind}|{path}";
        var fp = Fingerprint(path);
        var gate = _locks.GetOrAdd(key, _ => new object());
        lock (gate)
        {
            var now = _time.GetUtcNow();
            if (_map.TryGetValue(key, out var e) && e.Fingerprint == fp && now - e.At < Ttl && e.Value is T hit) return hit;
            var value = factory();                       // отмена/ошибка: исключение уходит, запись не создаётся
            _map[key] = new Entry(fp, now, value);
            Evict();
            return value;
        }
    }

    private void Evict()
    {
        if (_map.Count <= MaxEntries) return;
        foreach (var k in _map.OrderBy(kv => kv.Value.At).Take(_map.Count - MaxEntries).Select(kv => kv.Key).ToList())
            _map.TryRemove(k, out _);
    }

    /// <summary>Отпечаток дерева исходников: число файлов, самое позднее изменение, суммарный размер.</summary>
    internal static string Fingerprint(string path)
    {
        long count = 0, maxTicks = 0, size = 0;
        try
        {
            foreach (var f in RoslynScan.EnumerateCs(path))
            {
                try
                {
                    var fi = new FileInfo(f);
                    count++; size += fi.Length;
                    var t = fi.LastWriteTimeUtc.Ticks;
                    if (t > maxTicks) maxTicks = t;
                }
                catch { /* недоступный файл не должен ронять отпечаток */ }
            }
        }
        catch { /* нет папки — отпечаток пустой, разбор сам вернёт ошибку */ }
        return $"{count}:{maxTicks}:{size}";
    }
}

// Декораторы портов: тот же контракт, результат берётся из кеша. Ключ — вид источника + путь сервиса.
internal sealed class CachingTypeSource(ITypeSource inner, ScanCache cache) : ITypeSource
{
    public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) =>
        cache.GetOrAdd("types", path, () => inner.Read(path, ct));
}

internal sealed class CachingEndpointSource(IEndpointSource inner, ScanCache cache) : IEndpointSource
{
    public IReadOnlyList<EndpointHit> Read(string root, CancellationToken ct = default) =>
        cache.GetOrAdd("endpoints", root, () => inner.Read(root, ct));
}

internal sealed class CachingHttpCallSource(IHttpCallSource inner, ScanCache cache) : IHttpCallSource
{
    public IReadOnlyList<HttpCallHit> Read(string root, CancellationToken ct = default) =>
        cache.GetOrAdd("httpcalls", root, () => inner.Read(root, ct));
}

internal sealed class CachingConstructionSource(IConstructionSource inner, ScanCache cache) : IConstructionSource
{
    public IReadOnlyList<ConstructionSite> Read(string path, CancellationToken ct = default) =>
        cache.GetOrAdd("construction", path, () => inner.Read(path, ct));
}
