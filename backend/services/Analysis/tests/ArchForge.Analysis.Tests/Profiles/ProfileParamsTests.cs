using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;

namespace ArchForge.Analysis.Tests.Profiles;

public class ProfileParamsTests
{
    private sealed class FakeSource(SystemGraph graph) : ISystemSource
    {
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default) => graph;
    }

    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_pp_" + Guid.NewGuid().ToString("N"))).FullName;

    // ── имена сервисов ──

    [Fact]
    public void Naming_default_matches_previous_hardcoded_lists()
    {
        var d = ServiceNaming.Default;

        Assert.Equal(["-service", "-svc", "-api", "-worker", "-gateway", "-app"], d.TypeSuffixes);
        Assert.Equal(["-migrations", "-migration", "-migrate"], d.MigrationSuffixes);
        Assert.Equal(["migrat", "seed", "cron"], d.SatelliteMarkers);
        Assert.True(d.IsDefault);
    }

    [Fact]
    public void Naming_sanitised_drops_blanks_and_falls_back_when_empty()
    {
        var n = new ServiceNaming(["  ", new string('x', 100)], [], ["-core", "-CORE", " -edge "]).Sanitized();

        Assert.Equal(ServiceNaming.Default.TypeSuffixes, n.TypeSuffixes);       // всё отброшено → умолчание
        Assert.Equal(ServiceNaming.Default.MigrationSuffixes, n.MigrationSuffixes);
        Assert.Equal(["-core", "-edge"], n.SatelliteMarkers);                    // дубль без учёта регистра убран, пробелы срезаны
    }

    [Fact]
    public void Naming_list_size_is_capped()
    {
        var many = Enumerable.Range(0, 500).Select(i => "-s" + i).ToList();

        Assert.Equal(32, new ServiceNaming(many, many, many).Sanitized().TypeSuffixes.Count);
    }

    private static SystemGraph TwoNamesOneDb() => new(
        [
            new SystemNode("auth-api", "auth-api", SystemNodeKind.Service),   // без образа → идентичность по имени
            new SystemNode("auth-core", "auth-core", SystemNodeKind.Service),
            new SystemNode("db", "db", SystemNodeKind.Datastore, "postgres:17"),
        ],
        [
            new SystemEdge("auth-api", "db", "rw-db", EdgeVia.Config, 0.9),
            new SystemEdge("auth-core", "db", "rw-db", EdgeVia.Config, 0.9),
        ], []);

    [Fact]
    public void Shared_db_uses_type_suffixes_from_naming()
    {
        var g = TwoNamesOneDb();

        Assert.Contains(SharedDbDetector.Detect(g), p => p.Kind == "shared-db");              // «-core» не суффикс → два приложения
        var custom = new ServiceNaming([.. ServiceNaming.Default.TypeSuffixes, "-core"],
            ServiceNaming.Default.MigrationSuffixes, ServiceNaming.Default.SatelliteMarkers);
        Assert.Empty(SharedDbDetector.Detect(g, custom));                                      // «auth-api» ≡ «auth-core» → одно приложение
    }

    [Fact]
    public void Handler_recomputes_shared_db_with_profile_naming()
    {
        var dir = TempDir();
        var g = TwoNamesOneDb() with { Problems = [new SystemProblem(ProblemSeverity.Critical, "shared-db", "общая БД", "db")] };
        var profile = new ProjectProfile(Params: new ProfileParams(Naming: new ServiceNaming(
            [.. ServiceNaming.Default.TypeSuffixes, "-core"], ServiceNaming.Default.MigrationSuffixes, ServiceNaming.Default.SatelliteMarkers)));

        var dto = new GetSystemHandler(new FakeSource(g)).Handle(new GetSystemQuery(dir, Profile: profile))!;

        Assert.DoesNotContain(dto.Problems ?? [], p => p.Kind == "shared-db");
    }

    // ── свод: слои «умолчание → профиль → файл» ──

    [Fact]
    public void Default_profile_leaves_pack_unchanged()
    {
        var pack = ConventionPack.Default();

        Assert.Same(pack, pack.WithProfile(ProjectProfile.Default));
    }

    [Fact]
    public void Vocabulary_renames_role_markers_and_expectations()
    {
        var profile = new ProjectProfile(Vocabulary: new Dictionary<string, RoleVocabulary>
        {
            ["Command"] = new(["IRequest"], [], ["Request"]),
        });

        var pack = ConventionPack.Default().WithProfile(profile);

        Assert.Equal("Command", pack.Markers["IRequest"]);
        Assert.DoesNotContain("ICommand", pack.Markers.Keys);                       // прежнее имя роли заменено
        Assert.Contains(pack.Expect, e => e is { Suffix: "Request", Marker: "IRequest", Role: "Command" });
        Assert.DoesNotContain(pack.Expect, e => e.Suffix == "Command");
        Assert.Equal("Handler", pack.Markers["ICommandHandler"]);                   // другие роли не тронуты

        // и классификатор узнаёт команду по новому маркеру без дрейфа
        var raw = new RawType("CreateCourseRequest", "record", ["IRequest"], "(string N)", "Application", "x.cs");
        var result = new TypeClassifier().Classify([raw], pack);
        Assert.Empty(result.Drift);
    }

    [Fact]
    public void Dto_thresholds_come_from_profile()
    {
        var profile = new ProjectProfile(Params: new ProfileParams(DtoMinSites: 5, DtoMinFiles: 2));

        var rule = ConventionPack.Default().WithProfile(profile).DtoMapping;

        Assert.Equal(5, rule.MinSites);
        Assert.Equal(2, rule.MinFiles);
    }

    [Fact]
    public void Project_file_is_stronger_than_profile()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "archforge.conventions.json"),
            """{ "dtoMapping": { "minSites": 9 }, "markers": { "ICmd": "Command" } }""");
        var profile = new ProjectProfile(
            Params: new ProfileParams(DtoMinSites: 5, DtoMinFiles: 2),
            Vocabulary: new Dictionary<string, RoleVocabulary> { ["Command"] = new(["IRequest"], [], []) });

        var pack = ConventionPack.Load(dir, ConventionPack.Default().WithProfile(profile));

        Assert.Equal(9, pack.DtoMapping.MinSites);               // файл перебил профиль
        Assert.Equal(2, pack.DtoMapping.MinFiles);               // а чего в файле нет — осталось от профиля
        Assert.Equal("Command", pack.Markers["ICmd"]);           // маркер из файла
        Assert.Equal("Command", pack.Markers["IRequest"]);       // маркер из профиля сохранился
    }

    [Fact]
    public void Load_without_baseline_behaves_as_before()
    {
        var pack = ConventionPack.Load(TempDir());

        Assert.Equal("встроенный свод", pack.Source);
        Assert.Equal(3, pack.DtoMapping.MinSites);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, 1000)]
    public void Dto_thresholds_are_clamped(int input, int expected)
    {
        var th = new ProjectProfile(Params: new ProfileParams(DtoMinSites: input, DtoMinFiles: input)).Thresholds;

        Assert.Equal(expected, th.DtoMinSites);
        Assert.Equal(expected, th.DtoMinFiles);
    }

    [Fact]
    public void Vocabulary_is_bounded_and_tolerates_nulls()
    {
        var vocab = new Dictionary<string, RoleVocabulary>
        {
            ["Command"] = null!,                                   // null-значение не роняет
            [""] = new(["IBad"], [], []),                          // пустое имя роли пропущено
            [new string('r', 200)] = new(["ILong"], [], []),       // слишком длинное имя пропущено
        };
        for (var i = 0; i < 500; i++) vocab["Role" + i] = new(["IM" + i], [], []);

        var pack = ConventionPack.Default().WithProfile(new ProjectProfile(Vocabulary: vocab));

        Assert.DoesNotContain("IBad", pack.Markers.Keys);
        Assert.DoesNotContain("ILong", pack.Markers.Keys);
        Assert.Equal("Command", pack.Markers["ICommand"]);         // роль с null не тронута
        var added = pack.Markers.Keys.Count(k => k.StartsWith("IM", StringComparison.Ordinal));
        Assert.True(added <= ConventionPack.MaxRoles);             // лишние роли отброшены
    }

    [Fact]
    public void Empty_lists_in_a_role_keep_builtin_values()
    {
        // задали только суффикс: маркеры хендлера остаются, ожидание строится на них с новым суффиксом
        var profile = new ProjectProfile(Vocabulary: new Dictionary<string, RoleVocabulary>
        {
            ["Handler"] = new([], [], ["UseCase"]),
        });

        var pack = ConventionPack.Default().WithProfile(profile);

        Assert.Equal("Handler", pack.Markers["ICommandHandler"]);
        Assert.Equal("Handler", pack.Markers["IQueryHandler"]);
        Assert.Contains(pack.Expect, e => e is { Suffix: "UseCase", Role: "Handler" } && e.Marker.Contains("ICommandHandler"));
        Assert.DoesNotContain(pack.Expect, e => e is { Suffix: "Handler", Role: "Handler" });
    }

    [Fact]
    public void Only_markers_given_rewrites_expectation_text()
    {
        var profile = new ProjectProfile(Vocabulary: new Dictionary<string, RoleVocabulary>
        {
            ["Command"] = new(["IRequest"], [], []),
        });

        var pack = ConventionPack.Default().WithProfile(profile);

        Assert.Contains(pack.Expect, e => e is { Suffix: "Command", Marker: "IRequest", Role: "Command" }); // не «ожидается ICommand»
    }

    [Fact]
    public void Same_suffix_in_two_roles_leaves_single_expectation()
    {
        var profile = new ProjectProfile(Vocabulary: new Dictionary<string, RoleVocabulary>
        {
            ["Handler"] = new(["IHandler"], [], ["Command"]),   // суффикс уже принадлежит роли Command
        });

        var pack = ConventionPack.Default().WithProfile(profile);

        Assert.Single(pack.Expect, e => e.Suffix == "Command");
    }

    [Fact]
    public void Role_vocabulary_sanitised_trims_dedups_and_caps()
    {
        var many = Enumerable.Range(0, 200).Select(i => "M" + i).ToList();
        var v = new RoleVocabulary([" A ", "A", "", new string('x', 300), .. many], [], []).Sanitized();

        Assert.Equal(64, v.Markers.Count);
        Assert.Equal("A", v.Markers[0]);
        Assert.DoesNotContain(v.Markers, m => m.Length > 128 || m.Length == 0);
    }

    [Fact]
    public void Broken_project_file_keeps_profile_baseline_values()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "archforge.conventions.json"), "{ not json");
        var baseline = ConventionPack.Default().WithProfile(new ProjectProfile(Params: new ProfileParams(DtoMinSites: 7)));

        var pack = ConventionPack.Load(dir, baseline);

        Assert.Contains("ошибка чтения", pack.Source);
        Assert.Equal(7, pack.DtoMapping.MinSites);               // порог профиля не потерян из-за битого файла
    }
}
