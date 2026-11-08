using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;
using ArchForge.Map.Infrastructure;

namespace ArchForge.Map.Tests;

public class FileProfileStoreTests
{
    private static string Dir() => Path.Combine(Path.GetTempPath(), "archforge_store_" + Guid.NewGuid().ToString("N"));

    private static ConfigManifestDto Cfg(string id, int version = 1) =>
        new(id, "Конфигурация " + id, "@kapefa", version, new ProfileDto("Standard", new Dictionary<string, string> { ["drift"] = "Off" }),
            Kind: "own", CatalogVersion: "2026.11");

    [Fact]
    public async Task Data_survives_a_new_store_instance()
    {
        var dir = Dir();
        var a = new FileProfileStore(dir);
        await a.AddConfigAsync(Cfg("c1"));
        await a.SetBindingAsync("/p", new ProfileBindingDto(ConfigId: "c1"));

        var b = new FileProfileStore(dir);

        Assert.Equal("c1", (await b.GetConfigAsync("c1"))!.Id);
        Assert.Equal("Off", (await b.GetConfigAsync("c1"))!.Profile.Overrides!["drift"]);
        Assert.Equal("c1", (await b.GetBindingAsync("/p"))!.ConfigId);
        Assert.Equal(1, await b.CountBindingsAsync());
    }

    [Fact]
    public async Task Empty_store_returns_empty_results()
    {
        var s = new FileProfileStore(Dir());

        Assert.Empty(await s.ListConfigsAsync());
        Assert.Null(await s.GetConfigAsync("x"));
        Assert.Null(await s.GetBindingAsync("/p"));
    }

    [Fact]
    public async Task Duplicate_id_is_not_added()
    {
        var s = new FileProfileStore(Dir());
        Assert.True(await s.AddConfigAsync(Cfg("c1")));

        Assert.False(await s.AddConfigAsync(Cfg("c1")));
        Assert.Single(await s.ListConfigsAsync());
    }

    [Fact]
    public async Task Replace_checks_version()
    {
        var s = new FileProfileStore(Dir());
        await s.AddConfigAsync(Cfg("c1"));

        Assert.Equal(ReplaceResult.Conflict, await s.ReplaceConfigAsync("c1", 7, Cfg("c1", 8)));
        Assert.Equal(ReplaceResult.NotFound, await s.ReplaceConfigAsync("nope", 1, Cfg("nope", 2)));
        Assert.Equal(ReplaceResult.Ok, await s.ReplaceConfigAsync("c1", 1, Cfg("c1", 2)));
        Assert.Equal(2, (await s.GetConfigAsync("c1"))!.Version);
    }

    [Fact]
    public async Task Delete_removes_bindings_to_the_config_only()
    {
        var s = new FileProfileStore(Dir());
        await s.AddConfigAsync(Cfg("c1"));
        await s.SetBindingAsync("/a", new ProfileBindingDto(ConfigId: "c1"));
        await s.SetBindingAsync("/b", new ProfileBindingDto(Level: "Simple"));

        Assert.True(await s.DeleteConfigAsync("c1"));

        Assert.Null(await s.GetBindingAsync("/a"));
        Assert.NotNull(await s.GetBindingAsync("/b"));
        Assert.False(await s.DeleteConfigAsync("c1"));
    }

    [Fact]
    public async Task Binding_null_removes_it()
    {
        var s = new FileProfileStore(Dir());
        await s.SetBindingAsync("/a", new ProfileBindingDto(Level: "Simple"));
        await s.SetBindingAsync("/a", null);

        Assert.Equal(0, await s.CountBindingsAsync());
    }

    [Fact]
    public async Task Corrupt_file_is_reported_and_left_untouched()
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "profiles.json");
        File.WriteAllText(file, "{ broken");
        var s = new FileProfileStore(dir);

        await Assert.ThrowsAsync<InvalidDataException>(() => s.ListConfigsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => s.AddConfigAsync(Cfg("c1")));   // запись тоже не затирает чужие данные

        Assert.Equal("{ broken", File.ReadAllText(file));
    }

    [Fact]
    public async Task Parallel_writes_do_not_lose_data_or_corrupt_the_file()
    {
        var s = new FileProfileStore(Dir());

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => s.AddConfigAsync(Cfg("c" + i))));

        Assert.Equal(30, (await s.ListConfigsAsync()).Count);
    }

    [Fact]
    public async Task No_temp_file_is_left_after_save()
    {
        var dir = Dir();
        var s = new FileProfileStore(dir);
        await s.AddConfigAsync(Cfg("c1"));

        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}
