using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;

namespace ArchForge.Map.Core.Profiles;

/// <summary>Откуда Map берёт каталог правил (нужен для проверки профиля и версии каталога в манифесте).</summary>
public interface IRuleCatalogProvider
{
    Task<RuleCatalogDto?> GetAsync(CancellationToken ct = default);
}

/// <summary>Каталог у Analysis, с коротким кешем: каталог не зависит от проекта и меняется только с релизом.</summary>
public sealed class AnalysisRuleCatalogProvider(AnalysisClient analysis, TimeProvider? time = null) : IRuleCatalogProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RuleCatalogDto? _cached;
    private DateTimeOffset _at;

    public async Task<RuleCatalogDto?> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && _time.GetUtcNow() - _at < Ttl) return _cached;
            var fresh = await analysis.GetRulesAsync(ct);
            if (fresh is not null) { _cached = fresh; _at = _time.GetUtcNow(); }
            return fresh ?? _cached; // Analysis недоступен — отдаём прошлый каталог, если он был
        }
        finally { _gate.Release(); }
    }
}
