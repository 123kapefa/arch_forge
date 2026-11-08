using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;

namespace ArchForge.Map.Tests;

internal sealed class MemoryStore : IProfileStore
{
    public List<ConfigManifestDto> Configs { get; } = [];
    public Dictionary<string, ProfileBindingDto> Bindings { get; } = new();
    public bool Throw { get; set; }
    private void Check() { if (Throw) throw new InvalidDataException("файл повреждён"); }

    public Task<IReadOnlyList<ConfigManifestDto>> ListConfigsAsync(CancellationToken ct = default) { Check(); return Task.FromResult<IReadOnlyList<ConfigManifestDto>>([.. Configs]); }
    public Task<ConfigManifestDto?> GetConfigAsync(string id, CancellationToken ct = default) { Check(); return Task.FromResult(Configs.FirstOrDefault(c => c.Id == id)); }
    public Task<bool> AddConfigAsync(ConfigManifestDto config, CancellationToken ct = default) { Check(); if (Configs.Any(c => c.Id == config.Id)) return Task.FromResult(false); Configs.Add(config); return Task.FromResult(true); }
    public Task<ReplaceResult> ReplaceConfigAsync(string id, int expectedVersion, ConfigManifestDto r, CancellationToken ct = default)
    {
        Check();
        var i = Configs.FindIndex(c => c.Id == id);
        if (i < 0) return Task.FromResult(ReplaceResult.NotFound);
        if (Configs[i].Version != expectedVersion) return Task.FromResult(ReplaceResult.Conflict);
        Configs[i] = r;
        return Task.FromResult(ReplaceResult.Ok);
    }
    public Task<bool> DeleteConfigAsync(string id, CancellationToken ct = default)
    {
        Check();
        if (Configs.RemoveAll(c => c.Id == id) == 0) return Task.FromResult(false);
        foreach (var k in Bindings.Where(kv => kv.Value.ConfigId == id).Select(kv => kv.Key).ToList()) Bindings.Remove(k);
        return Task.FromResult(true);
    }
    public Task<ProfileBindingDto?> GetBindingAsync(string p, CancellationToken ct = default) { Check(); return Task.FromResult(Bindings.GetValueOrDefault(p)); }
    public Task SetBindingAsync(string p, ProfileBindingDto? b, CancellationToken ct = default) { Check(); if (b is null) Bindings.Remove(p); else Bindings[p] = b; return Task.CompletedTask; }
    public Task<int> CountBindingsAsync(CancellationToken ct = default) => Task.FromResult(Bindings.Count);
}


internal sealed class FakeCatalog(RuleCatalogDto? dto) : IRuleCatalogProvider
{
    public Task<RuleCatalogDto?> GetAsync(CancellationToken ct = default) => Task.FromResult(dto);
}
