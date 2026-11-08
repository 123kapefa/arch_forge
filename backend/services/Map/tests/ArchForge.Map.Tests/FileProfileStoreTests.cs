using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;
using ArchForge.Map.Infrastructure;

namespace ArchForge.Map.Tests;

public class FileProfileStoreTests
{
    private const int Max = 200;
    private static readonly IReadOnlySet<string> Builtin = new HashSet<string> { "lvl-simple", "lvl-standard", "lvl-platform" };

    private static string Dir() => Path.Combine(Path.GetTempPath(), "archforge_store_" + Guid.NewGuid().ToString("N"));

    private static ConfigManifestDto Cfg(string id, int version = 1) =>
        new(id, "Конфигурация " + id, "@kapefa", version, new ProfileDto("Standard", new Dictionary<string, string> { ["drift"] = "Off" }),
            Kind: "own", CatalogVersion: "2026.11");

    private static Task<AddResult> Add(FileProfileStore s, string id, int max = Max) => s.AddConfigAsync(Cfg(id), max);
    private static Task<BindResult> Bind(FileProfileStore s, string path, ProfileBindingDto b, int max = 1000) =>
        s.TrySetBindingAsync(path, b, max, Builtin);

    [Fact]
    public async Task Data_survives_a_new_store_instance()
    {
        var dir = Dir();
        var a = new FileProfileStore(dir);
        await Add(a, "c1");
        await Bind(a, "/p", new ProfileBindingDto(ConfigId: "c1"));

        var b = new FileProfileStore(dir);

        Assert.Equal("c1", (await b.GetConfigAsync("c1"))!.Id);
        Assert.Equal("Off", (await b.GetConfigAsync("c1"))!.Profile.Overrides!["drift"]);
        Assert.Equal("c1", (await b.GetBindingAsync("/p"))!.ConfigId);
    }

    [Fact]
    public async Task Cyrillic_is_written_as_is_not_escaped()
    {
        var dir = Dir();
        var s = new FileProfileStore(dir);
        await Add(s, "c1");

        var text = File.ReadAllText(Path.Combine(dir, "profiles.json"));

        Assert.Contains("Конфигурация", text);
        Assert.DoesNotContain("\\u04", text);
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
    public async Task Add_reports_duplicate_id_and_limit()
    {
        var s = new FileProfileStore(Dir());

        Assert.Equal(AddResult.Ok, await Add(s, "c1", max: 2));
        Assert.Equal(AddResult.IdTaken, await Add(s, "c1", max: 2));
        Assert.Equal(AddResult.Ok, await Add(s, "c2", max: 2));
        Assert.Equal(AddResult.LimitReached, await Add(s, "c3", max: 2));
        Assert.Equal(2, (await s.ListConfigsAsync()).Count);
    }

    [Fact]
    public async Task Limit_holds_under_parallel_adds()
    {
        var s = new FileProfileStore(Dir());

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Add(s, "c" + i, max: 10)));

        Assert.Equal(10, results.Count(r => r == AddResult.Ok));       // не 11 и не 40: проверка атомарна с записью
        Assert.Equal(10, (await s.ListConfigsAsync()).Count);
    }

    [Fact]
    public async Task Binding_checks_config_exists_and_limit_atomically()
    {
        var s = new FileProfileStore(Dir());
        await Add(s, "c1");

        Assert.Equal(BindResult.ConfigMissing, await Bind(s, "/a", new ProfileBindingDto(ConfigId: "ghost")));
        Assert.Equal(BindResult.Ok, await Bind(s, "/a", new ProfileBindingDto(ConfigId: "lvl-platform")));   // встроенный уровень — можно
        Assert.Equal(BindResult.Ok, await Bind(s, "/b", new ProfileBindingDto(ConfigId: "c1"), max: 2));
        Assert.Equal(BindResult.LimitReached, await Bind(s, "/c", new ProfileBindingDto(Level: "Simple"), max: 2));
        Assert.Equal(BindResult.Ok, await Bind(s, "/a", new ProfileBindingDto(Level: "Simple"), max: 2));      // существующую менять можно

        var par = new FileProfileStore(Dir());
        var many = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Bind(par, "/p" + i, new ProfileBindingDto(Level: "Simple"), max: 5)));
        Assert.Equal(5, many.Count(r => r == BindResult.Ok));
    }

    [Fact]
    public async Task Replace_checks_version()
    {
        var s = new FileProfileStore(Dir());
        await Add(s, "c1");

        Assert.Equal(ReplaceResult.Conflict, await s.ReplaceConfigAsync("c1", 7, Cfg("c1", 8)));
        Assert.Equal(ReplaceResult.NotFound, await s.ReplaceConfigAsync("nope", 1, Cfg("nope", 2)));
        Assert.Equal(ReplaceResult.Ok, await s.ReplaceConfigAsync("c1", 1, Cfg("c1", 2)));
        Assert.Equal(2, (await s.GetConfigAsync("c1"))!.Version);
    }

    [Fact]
    public async Task Delete_removes_bindings_to_the_config_only()
    {
        var s = new FileProfileStore(Dir());
        await Add(s, "c1");
        await Bind(s, "/a", new ProfileBindingDto(ConfigId: "c1"));
        await Bind(s, "/b", new ProfileBindingDto(Level: "Simple"));

        Assert.True(await s.DeleteConfigAsync("c1"));

        Assert.Null(await s.GetBindingAsync("/a"));
        Assert.NotNull(await s.GetBindingAsync("/b"));
        Assert.False(await s.DeleteConfigAsync("c1"));
    }

    [Fact]
    public async Task Clear_binding_removes_it()
    {
        var s = new FileProfileStore(Dir());
        await Bind(s, "/a", new ProfileBindingDto(Level: "Simple"));
        await s.ClearBindingAsync("/a");

        Assert.Null(await s.GetBindingAsync("/a"));
    }

    [Fact]
    public async Task Corrupt_file_is_reported_without_path_and_left_untouched()
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "profiles.json");
        File.WriteAllText(file, "{ broken");
        var s = new FileProfileStore(dir);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => s.ListConfigsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => Add(s, "c1"));          // запись тоже не затирает чужие данные

        Assert.DoesNotContain(dir, ex.Message);                                       // путь не уходит клиенту
        Assert.Equal(dir.Length > 0 ? file : "", ex.Data["path"]);                    // а в Data — для лога
        Assert.Equal("{ broken", File.ReadAllText(file));
    }

    [Fact]
    public async Task File_from_a_newer_format_is_not_overwritten()
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "profiles.json");
        File.WriteAllText(file, """{ "version": 9, "configs": [], "bindings": {} }""");
        var s = new FileProfileStore(dir);

        await Assert.ThrowsAsync<InvalidDataException>(() => Add(s, "c1"));

        Assert.Contains("\"version\": 9", File.ReadAllText(file));
    }

    [Fact]
    public async Task Null_entries_in_valid_json_do_not_crash_the_store()
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profiles.json"), """{ "version": 1, "configs": [null], "bindings": { "/x": null } }""");
        var s = new FileProfileStore(dir);

        Assert.Empty(await s.ListConfigsAsync());
        Assert.Null(await s.GetBindingAsync("/x"));
        Assert.False(await s.DeleteConfigAsync("any"));
    }

    [Fact]
    public async Task No_temp_files_are_left_after_save()
    {
        var dir = Dir();
        var s = new FileProfileStore(dir);
        await Add(s, "c1");

        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}
