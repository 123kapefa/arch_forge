using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;

namespace ArchForge.Map.Tests;

public class ProfileServiceTests
{
    private static readonly RuleCatalogDto Catalog = new("2026.11",
    [
        new RuleDto("drift", "convention", "Дрейф", "n", new Dictionary<string, string?> { ["Simple"] = "Off", ["Standard"] = null, ["Platform"] = null }),
        new RuleDto("service-cycle", "architecture", "Цикл", "n", new Dictionary<string, string?> { ["Simple"] = null, ["Standard"] = null, ["Platform"] = null }),
    ]);

    private static (ProfileService svc, MemoryStore store) Make(RuleCatalogDto? catalog = null, bool noCatalog = false)
    {
        var store = new MemoryStore();
        return (new ProfileService(store, new FakeCatalog(noCatalog ? null : catalog ?? Catalog)), store);
    }

    private static ConfigManifestDto Cfg(string name = "Для клиента Альфа", ProfileDto? profile = null, int version = 1, string? parent = null) =>
        new(null, name, "@kapefa", version, profile ?? new ProfileDto("Standard", new Dictionary<string, string> { ["drift"] = "Warning" }), ParentId: parent);

    private static string Proj(string name = "proj") => Path.Combine(Path.GetTempPath(), name);

    // ── конфигурации ──

    [Fact]
    public async Task List_contains_builtin_levels_first_and_marks_them_read_only()
    {
        var (svc, _) = Make();

        var list = await svc.ListAsync();

        Assert.Equal(["lvl-simple", "lvl-standard", "lvl-platform"], list.Take(3).Select(c => c.Id));
        Assert.All(list.Take(3), c => Assert.Equal("level", c.Kind));
    }

    [Fact]
    public async Task Create_keeps_custom_rules_and_rejects_broken_ones()
    {
        var (svc, _) = Make();
        var ok = new CustomRuleDto("handlers", "suffix-base", "Хендлеры", "Warning", Suffix: "Handler", BaseType: "IHandler");

        var created = await svc.CreateAsync(Cfg(profile: new ProfileDto("Standard", CustomRules: [ok])));
        var broken = await svc.CreateAsync(Cfg(profile: new ProfileDto("Standard", CustomRules: [ok with { BaseType = null }])));

        Assert.True(created.Ok);
        Assert.Equal("handlers", Assert.Single(created.Value!.Profile.CustomRules!).Id);
        var lower = await svc.CreateAsync(Cfg("Lower", new ProfileDto("Standard", CustomRules: [ok with { Strictness = "error" }])));
        Assert.Equal("Error", lower.Value!.Profile.CustomRules![0].Strictness);   // канонический вид, как у overrides
        Assert.Equal(400, broken.Status);
        Assert.Contains(broken.Errors, e => e.Contains("BaseType"));
    }

    [Fact]
    public async Task Create_stamps_server_fields()
    {
        var (svc, store) = Make();

        var o = await svc.CreateAsync(Cfg());

        Assert.True(o.Ok);
        Assert.Equal(201, o.Status);
        var c = o.Value!;
        Assert.False(string.IsNullOrEmpty(c.Id));
        Assert.Equal(1, c.Version);
        Assert.Equal("own", c.Kind);
        Assert.Equal("2026.11", c.CatalogVersion);       // версия каталога берётся у Analysis, а не от клиента
        Assert.Equal(1, c.FormatVersion);
        Assert.NotNull(c.UpdatedAt);
        Assert.Single(store.Configs);
    }

    [Fact]
    public async Task Create_ignores_client_supplied_server_fields()
    {
        var (svc, _) = Make();
        var evil = Cfg() with { Id = "lvl-standard", Kind = "level", CatalogVersion = "x", FormatVersion = 99, Version = 50 };

        var c = (await svc.CreateAsync(evil)).Value!;

        Assert.NotEqual("lvl-standard", c.Id);
        Assert.Equal("own", c.Kind);
        Assert.Equal("2026.11", c.CatalogVersion);
        Assert.Equal(1, c.FormatVersion);
        Assert.Equal(1, c.Version);
    }

    [Fact]
    public async Task Create_rejects_unknown_rule_and_bad_level_with_400()
    {
        var (svc, store) = Make();

        var o = await svc.CreateAsync(Cfg(profile: new ProfileDto("Huge", new Dictionary<string, string> { ["ghost"] = "Off" })));

        Assert.Equal(400, o.Status);
        Assert.True(o.Errors.Count >= 2);
        Assert.Empty(store.Configs);
    }

    [Fact]
    public async Task Create_503_when_analysis_catalog_unavailable()
    {
        var (svc, _) = Make(noCatalog: true);

        Assert.Equal(503, (await svc.CreateAsync(Cfg())).Status);
    }

    [Fact]
    public async Task Create_rejects_unknown_parent()
    {
        var (svc, _) = Make();

        Assert.Equal(400, (await svc.CreateAsync(Cfg(parent: "nope"))).Status);
    }

    [Fact]
    public async Task Update_bumps_version_and_detects_conflict()
    {
        var (svc, _) = Make();
        var created = (await svc.CreateAsync(Cfg())).Value!;

        var u1 = await svc.UpdateAsync(created.Id!, created with { Name = "Новое имя" });
        Assert.True(u1.Ok);
        Assert.Equal(2, u1.Value!.Version);

        var stale = await svc.UpdateAsync(created.Id!, created with { Name = "Опоздал" }); // клиент держит версию 1
        Assert.Equal(409, stale.Status);
    }

    [Fact]
    public async Task Update_requires_version_and_known_parent_and_takes_id_from_url()
    {
        var (svc, _) = Make();
        var created = (await svc.CreateAsync(Cfg())).Value!;

        Assert.Equal(400, (await svc.UpdateAsync(created.Id!, created with { Version = 0 })).Status);          // забытая версия — 400, а не «конфликт»
        Assert.Equal(400, (await svc.UpdateAsync(created.Id!, created with { ParentId = "ghost" })).Status);
        var u = (await svc.UpdateAsync(created.Id!, created with { Id = "lvl-standard" })).Value!;              // Id из тела игнорируется
        Assert.Equal(created.Id, u.Id);
    }

    [Fact]
    public async Task Strictness_values_are_stored_in_canonical_case()
    {
        var (svc, _) = Make();

        var c = (await svc.CreateAsync(Cfg(profile: new ProfileDto("standard", new Dictionary<string, string> { ["drift"] = "warning" })))).Value!;

        Assert.Equal("Standard", c.Profile.Level);
        Assert.Equal("Warning", c.Profile.Overrides!["drift"]);
    }

    [Fact]
    public async Task Oversized_profile_is_rejected()
    {
        var (svc, store) = Make();
        var big = new ProfileDto("Standard", Vocabulary: Enumerable.Range(0, 60).ToDictionary(i => "Role" + i,
            _ => new RoleVocabularyDto([.. Enumerable.Range(0, 60).Select(j => new string('я', 120))])));

        var o = await svc.CreateAsync(Cfg(profile: big));

        Assert.Equal(400, o.Status);
        Assert.Empty(store.Configs);
    }

    [Fact]
    public async Task Update_unknown_is_404_and_builtin_level_is_403()
    {
        var (svc, _) = Make();

        Assert.Equal(404, (await svc.UpdateAsync("ghost", Cfg())).Status);
        Assert.Equal(403, (await svc.UpdateAsync("lvl-standard", Cfg())).Status);
        Assert.Equal(403, (await svc.DeleteAsync("lvl-simple")).Status);
    }

    [Fact]
    public async Task Duplicate_creates_copy_with_parent_and_fresh_version()
    {
        var (svc, _) = Make();
        var src = (await svc.CreateAsync(Cfg())).Value!;
        await svc.UpdateAsync(src.Id!, src);                       // версия источника 2

        var copy = (await svc.DuplicateAsync(src.Id!, "@marta")).Value!;

        Assert.NotEqual(src.Id, copy.Id);
        Assert.StartsWith("Копия: ", copy.Name);
        Assert.Equal(1, copy.Version);
        Assert.Equal(src.Id, copy.ParentId);
        Assert.Equal("@marta", copy.Author);
    }

    [Fact]
    public async Task Duplicate_of_builtin_level_works()
    {
        var (svc, _) = Make();

        var copy = (await svc.DuplicateAsync("lvl-platform", null)).Value!;

        Assert.Equal("Platform", copy.Profile.Level);
        Assert.Equal("lvl-platform", copy.ParentId);
    }

    [Fact]
    public async Task Config_count_is_capped()
    {
        var (svc, store) = Make();
        for (var i = 0; i < ProfileService.MaxOwnConfigs; i++) store.Configs.Add(Cfg("c" + i) with { Id = "id" + i });

        Assert.Equal(409, (await svc.CreateAsync(Cfg())).Status);
    }

    // ── привязка проекта ──

    [Fact]
    public async Task Project_without_binding_gets_standard_by_default()
    {
        var (svc, _) = Make();

        var r = (await svc.GetForPathAsync(Proj())).Value!;

        Assert.Equal("Standard", r.Effective.Level);
        Assert.Null(r.Config);
    }

    [Fact]
    public async Task Bind_level_and_config_round_trip()
    {
        var (svc, _) = Make();
        var cfg = (await svc.CreateAsync(Cfg())).Value!;

        var byLevel = (await svc.BindProjectAsync(Proj(), new ProfileBindingDto(Level: "platform"))).Value!;
        Assert.Equal("Platform", byLevel.Effective.Level);                 // регистр приведён
        Assert.Equal("Platform", byLevel.Binding.Level);

        var byCfg = (await svc.BindProjectAsync(Proj(), new ProfileBindingDto(ConfigId: cfg.Id))).Value!;
        Assert.Equal(cfg.Id, byCfg.Config!.Id);
        Assert.Equal("Warning", byCfg.Effective.Overrides!["drift"]);

        var again = (await svc.GetForPathAsync(Proj())).Value!;
        Assert.Equal(cfg.Id, again.Binding.ConfigId);
    }

    [Fact]
    public async Task Bind_requires_exactly_one_target_and_valid_values()
    {
        var (svc, _) = Make();

        Assert.Equal(400, (await svc.BindProjectAsync(Proj(), new ProfileBindingDto("x", "Simple"))).Status);
        Assert.Equal(400, (await svc.BindProjectAsync(Proj(), new ProfileBindingDto(Level: "Giant"))).Status);
        Assert.Equal(404, (await svc.BindProjectAsync(Proj(), new ProfileBindingDto(ConfigId: "ghost"))).Status);
        Assert.Equal(400, (await svc.BindProjectAsync(Proj(), null)).Status);      // пустое тело — ошибка, а не сброс
        Assert.Equal(400, (await svc.BindProjectAsync("  ", new ProfileBindingDto(Level: "Simple"))).Status);
        Assert.Equal(400, (await svc.BindProjectAsync("a\0b", new ProfileBindingDto(Level: "Simple"))).Status);
        Assert.Equal(400, (await svc.BindProjectAsync(new string('p', 2000), new ProfileBindingDto(Level: "Simple"))).Status);
    }

    [Fact]
    public async Task Empty_binding_resets_project_to_default()
    {
        var (svc, store) = Make();
        await svc.BindProjectAsync(Proj(), new ProfileBindingDto(Level: "Simple"));

        var r = (await svc.BindProjectAsync(Proj(), new ProfileBindingDto())).Value!;   // {} = сброс

        Assert.Equal("Standard", r.Effective.Level);
        Assert.Empty(store.Bindings);
    }

    [Fact]
    public async Task Path_is_normalised_so_trailing_slash_is_the_same_project()
    {
        var (svc, store) = Make();
        await svc.BindProjectAsync(Proj() + Path.DirectorySeparatorChar, new ProfileBindingDto(Level: "Simple"));

        var r = (await svc.GetForPathAsync(Proj())).Value!;

        Assert.Equal("Simple", r.Effective.Level);
        Assert.Single(store.Bindings);
    }

    [Fact]
    public async Task Deleting_config_returns_its_projects_to_default()
    {
        var (svc, store) = Make();
        var cfg = (await svc.CreateAsync(Cfg())).Value!;
        await svc.BindProjectAsync(Proj(), new ProfileBindingDto(ConfigId: cfg.Id));

        Assert.True((await svc.DeleteAsync(cfg.Id!)).Ok);

        Assert.Empty(store.Bindings);
        Assert.Equal("Standard", (await svc.GetForPathAsync(Proj())).Value!.Effective.Level);
    }

    [Fact]
    public async Task Bindings_count_is_capped()
    {
        var (svc, store) = Make();
        for (var i = 0; i < ProfileService.MaxBindings; i++) store.Bindings["/p" + i] = new ProfileBindingDto(Level: "Simple");

        Assert.Equal(409, (await svc.BindProjectAsync(Proj("new"), new ProfileBindingDto(Level: "Simple"))).Status);
        Assert.True((await svc.BindProjectAsync("/p0", new ProfileBindingDto(Level: "Platform"))).Ok); // существующую можно менять
    }

    // ── профиль для разбора ──

    [Fact]
    public async Task EffectiveFor_is_null_without_binding_so_analysis_keeps_old_behaviour()
    {
        var (svc, _) = Make();

        Assert.Null(await svc.EffectiveForAsync(Proj()));
    }

    [Fact]
    public async Task EffectiveFor_returns_bound_profile()
    {
        var (svc, _) = Make();
        var cfg = (await svc.CreateAsync(Cfg())).Value!;
        await svc.BindProjectAsync(Proj(), new ProfileBindingDto(ConfigId: cfg.Id));

        var p = await svc.EffectiveForAsync(Proj());

        Assert.Equal("Warning", p!.Overrides!["drift"]);
    }

    [Fact]
    public async Task EffectiveFor_does_not_break_analysis_when_store_fails()
    {
        var (svc, store) = Make();
        store.Throw = true;

        Assert.Null(await svc.EffectiveForAsync(Proj()));
    }

    [Fact]
    public void Path_key_ignores_trailing_separator_and_case_on_case_insensitive_systems()
    {
        var a = ProfileService.NormalizePath("/Users/X/Proj/");
        var b = ProfileService.NormalizePath("/Users/X/Proj");
        var c = ProfileService.NormalizePath("/users/x/proj");

        Assert.Equal(a, b);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) Assert.Equal(a, c);
        else Assert.NotEqual(a, c);
    }

    [Fact]
    public void Path_key_follows_symlink_of_project_folder()
    {
        var real = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "af_real_" + Guid.NewGuid().ToString("N"))).FullName;
        var link = Path.Combine(Path.GetTempPath(), "af_link_" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }   // симлинки недоступны

        Assert.Equal(ProfileService.NormalizePath(real), ProfileService.NormalizePath(link));
    }

    [Fact]
    public async Task Missing_analysis_catalog_never_throws_out_of_the_service()
    {
        var (svc, _) = Make(noCatalog: true);

        Assert.Equal(503, (await svc.UpdateAsync("x", Cfg())).Status);
    }
}
