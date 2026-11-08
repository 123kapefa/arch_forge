using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Features.Systems;
using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;

namespace ArchForge.Analysis.Tests.Profiles;

public class PreviewImpactTests
{
    private sealed class FakeSource(SystemGraph graph) : ISystemSource
    {
        public SystemGraph Read(string root, string? env = null, CancellationToken ct = default) => graph;
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredService> svcs) : IServiceDiscoverySource
    {
        public IReadOnlyList<DiscoveredService> Discover(string root) => svcs;
    }

    private sealed class FakeTypes(IReadOnlyList<RawType> raw) : ITypeSource
    {
        public IReadOnlyList<RawType> Read(string path, CancellationToken ct = default) => raw;
    }

    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_pv_" + Guid.NewGuid().ToString("N"))).FullName;

    private static SystemNode Svc(string id) => new(id, id, SystemNodeKind.Service);

    private static SystemProblem P(string kind, ProblemSeverity sev, string cat = ProblemCategory.Architecture) => new(sev, kind, kind, "loc", cat);

    private static PreviewImpactHandler Handler(string dir, SystemGraph graph, params RawType[] raw)
    {
        var analyzer = new ServiceAnalyzer(
            new FakeDiscovery([new DiscoveredService("Svc.A", Path.Combine(dir, "svc-a"), "docker-compose.yml")]),
            new FakeTypes(raw), new TypeClassifier());
        var system = new GetSystemHandler(new FakeSource(graph), analyzer);
        return new PreviewImpactHandler(system, new FakeSource(graph), analyzer);
    }

    private static RawType T(string name, string layer = "Application", params string[] bases) =>
        new(name, "class", bases, "", layer, name + ".cs");

    [Fact]
    public void Counts_findings_by_severity_and_rule()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], [P("shared-db", ProblemSeverity.Critical), P("exposed-datastore", ProblemSeverity.Warning)]);

        var pv = Handler(dir, graph).Handle(new PreviewQuery(dir, ProjectProfile.Default))!;

        Assert.Equal(1, pv.Current.Errors);
        Assert.Equal(1, pv.Current.Warnings);
        Assert.Equal(1, pv.Current.Rules["shared-db"].Count);
        Assert.True(pv.Current.Rules["shared-db"].Enabled);
        Assert.Equal(0, pv.Current.Rules["service-cycle"].Count);
    }

    [Fact]
    public void Override_changes_current_but_not_baseline()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], [P("shared-db", ProblemSeverity.Critical)]);
        var profile = new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["shared-db"] = Strictness.Off });

        var pv = Handler(dir, graph).Handle(new PreviewQuery(dir, profile))!;

        Assert.Equal(0, pv.Current.Errors);
        Assert.False(pv.Current.Rules["shared-db"].Enabled);              // выключено видно, а не молчит
        Assert.Equal(1, pv.Baseline.Errors);                              // чистый уровень по-прежнему видит находку
        Assert.True(pv.Baseline.Rules["shared-db"].Enabled);
    }

    [Fact]
    public void Remap_severity_moves_counts_between_buckets()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], [P("shared-db", ProblemSeverity.Critical)]);
        var profile = new ProjectProfile(Overrides: new Dictionary<string, Strictness> { ["shared-db"] = Strictness.Info });

        var pv = Handler(dir, graph).Handle(new PreviewQuery(dir, profile))!;

        Assert.Equal(0, pv.Current.Errors);
        Assert.Equal(1, pv.Current.Infos);
    }

    [Fact]
    public void Simple_level_turns_off_drift_and_baseline_follows_the_same_level()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], []);
        // тип без маркера → дрейф «ожидается ICommandHandler/IQueryHandler»
        var handler = Handler(dir, graph, T("RegisterHandler"));

        var standard = handler.Handle(new PreviewQuery(dir, ProjectProfile.Default))!;
        var simple = handler.Handle(new PreviewQuery(dir, new ProjectProfile(ProjectLevel.Simple)))!;

        Assert.True(standard.Current.Rules["drift"].Count >= 1);
        Assert.Equal(0, simple.Current.Rules["drift"].Count);
        Assert.False(simple.Current.Rules["drift"].Enabled);
    }

    [Fact]
    public void Drift_counts_every_item_not_one_per_service()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], []);
        var handler = Handler(dir, graph, T("AHandler"), T("BHandler"), T("CHandler"));

        var pv = handler.Handle(new PreviewQuery(dir, ProjectProfile.Default))!;

        Assert.Equal(3, pv.Current.Rules["drift"].Count);
    }

    [Fact]
    public void Marker_and_suffix_counts_come_from_project_types_and_skip_shared_and_tests()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], []);
        var handler = Handler(dir, graph,
            T("CreateACommand", "Application", "ICommand"),
            T("CreateBCommand", "Application", "ICommand"),
            T("PlainCommand", "Application"),
            T("SharedCommand", "Shared", "ICommand"),          // shared kernel не считается
            T("TestCommand", "tests", "ICommand"));

        var pv = handler.Handle(new PreviewQuery(dir, ProjectProfile.Default))!;

        Assert.Equal(2, pv.MarkerCounts["ICommand"]);
        Assert.Equal(0, pv.MarkerCounts["IQuery"]);
        Assert.Equal(3, pv.SuffixCounts["Command"]);
    }

    [Fact]
    public void Vocabulary_marker_is_counted_and_removes_drift()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], []);
        var handler = Handler(dir, graph, T("CreateCourseCommand", "Application", "IRequest"));
        var profile = new ProjectProfile(Vocabulary: new Dictionary<string, RoleVocabulary> { ["Command"] = new(["IRequest"], [], []) });

        var before = handler.Handle(new PreviewQuery(dir, ProjectProfile.Default))!;
        var after = handler.Handle(new PreviewQuery(dir, profile))!;

        Assert.Equal(1, before.Current.Rules["drift"].Count);              // IRequest не маркер свода → расхождение
        Assert.Equal(0, after.Current.Rules["drift"].Count);               // словарь объяснил проекту, что IRequest — это команда
        Assert.Equal(1, after.MarkerCounts["IRequest"]);
    }

    [Fact]
    public void Missing_or_non_directory_path_gives_null()
    {
        var graph = new SystemGraph([], [], []);
        var h = new PreviewImpactHandler(new GetSystemHandler(new FakeSource(graph)), new FakeSource(graph));

        Assert.Null(h.Handle(new PreviewQuery("/no/such/dir_archforge", ProjectProfile.Default)));
        Assert.Null(h.Handle(new PreviewQuery("", ProjectProfile.Default)));
    }

    [Fact]
    public void Works_without_analyzer_and_hint_has_reasons()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("a"), Svc("b"), Svc("c"), Svc("d"), new("t", "t", SystemNodeKind.Topic)], [], []);
        var h = new PreviewImpactHandler(new GetSystemHandler(new FakeSource(graph)), new FakeSource(graph));

        var pv = h.Handle(new PreviewQuery(dir, ProjectProfile.Default))!;

        Assert.Empty(pv.MarkerCounts.Where(kv => kv.Value > 0));
        Assert.Contains(pv.Hint.Reasons, r => r.Contains("4"));
        Assert.Contains(pv.Hint.Reasons, r => r.Contains("брокер"));
    }

    // ── подсказка уровня ──

    [Theory]
    [InlineData(1, false, 0, 0, "Simple")]
    [InlineData(0, false, 0, 0, "Simple")]
    [InlineData(1, false, 60, 100, "Standard")]     // один сервис, но с маркерами
    [InlineData(2, false, 0, 100, "Standard")]
    [InlineData(2, true, 0, 100, "Standard")]
    [InlineData(5, true, 70, 300, "Platform")]
    [InlineData(4, false, 55, 200, "Platform")]
    public void Level_hint_follows_signals(int services, bool broker, int markerPct, int types, string expected)
    {
        Assert.Equal(expected, LevelHint.Suggest(services, broker, markerPct, types).Level);
    }

    [Fact]
    public void Level_hint_ignores_marker_share_on_tiny_samples()
    {
        var h = LevelHint.Suggest(1, false, 100, 5);        // 5 типов — доля маркеров ничего не значит

        Assert.Equal("Simple", h.Level);
        Assert.DoesNotContain(h.Reasons, r => r.Contains("маркерные"));
    }

    [Fact]
    public void Level_hint_always_explains_itself()
    {
        Assert.All(new[] { LevelHint.Suggest(0, false, 0, 0), LevelHint.Suggest(9, true, 90, 900) }, h => Assert.NotEmpty(h.Reasons));
    }

    [Fact]
    public void Coverage_severity_problems_are_not_counted_as_findings()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [],
            [P("drift", ProblemSeverity.Coverage, ProblemCategory.Convention), P("shared-db", ProblemSeverity.Critical)]);

        var pv = Handler(dir, graph).Handle(new PreviewQuery(dir, ProjectProfile.Default))!;

        Assert.Equal(0, pv.Current.Rules["drift"].Count);
        var byRules = pv.Current.Rules.Values.Sum(r => r.Count);
        Assert.Equal(pv.Current.Errors + pv.Current.Warnings + pv.Current.Infos, byRules);   // сумма по правилам = сумме по строгости
    }

    private sealed class RecordingCache : IScanCacheControl
    {
        public List<string> Invalidated { get; } = [];
        public void InvalidateUnder(string root) => Invalidated.Add(root);
    }

    [Fact]
    public void Refresh_invalidates_the_scan_cache_under_the_project_root_only_when_asked()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("a")], [], []);
        var cache = new RecordingCache();
        var handler = new GetSystemHandler(new FakeSource(graph), null, null, cache);

        handler.Handle(new GetSystemQuery(dir));
        Assert.Empty(cache.Invalidated);                                  // обычный запрос кеш не трогает

        handler.Handle(new GetSystemQuery(dir, Refresh: true));
        Assert.Equal([dir], cache.Invalidated);
    }

    [Fact]
    public void Preview_never_refreshes_the_cache()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("a")], [], []);
        var cache = new RecordingCache();
        var system = new GetSystemHandler(new FakeSource(graph), null, null, cache);

        new PreviewImpactHandler(system, new FakeSource(graph)).Handle(new PreviewQuery(dir, ProjectProfile.Default));

        Assert.Empty(cache.Invalidated);                                  // иначе каждая правка профиля заново читала бы код
    }

    [Fact]
    public void Huge_vocabulary_on_many_types_stays_fast_and_correct()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], []);
        var raw = Enumerable.Range(0, 5000).Select(i => T($"Create{i}Command", "Application", i % 2 == 0 ? "ICommand" : "IRequest")).ToArray();
        // максимум, который пропускает валидатор: 64 роли × (32 маркера, 16 суффиксов); IRequest — маркер роли Command
        var vocab = Enumerable.Range(0, 64).ToDictionary(i => i == 0 ? "Command" : "Role" + i,
            i => new RoleVocabulary([.. Enumerable.Range(0, 31).Select(j => $"IMarker{i}_{j}"), i == 0 ? "IRequest" : "IExtra" + i], [], [.. Enumerable.Range(0, 16).Select(j => $"Suf{i}_{j}")]));
        var profile = new ProjectProfile(Vocabulary: vocab);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pv = Handler(dir, graph, raw).Handle(new PreviewQuery(dir, profile))!;
        sw.Stop();

        Assert.Equal(0, pv.MarkerCounts.GetValueOrDefault("ICommand"));       // ICommand заменён маркерами роли Command из профиля, в счётчиках его нет
        Assert.Equal(2500, pv.MarkerCounts["IRequest"]);                   // счётчики верны и при максимальном словаре
        Assert.Equal(0, pv.MarkerCounts["IMarker3_7"]);
        Assert.True(pv.SuffixCounts.ContainsKey("Suf0_0"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"слишком долго: {sw.Elapsed}");
    }

    [Fact]
    public void A_type_with_two_matching_markers_is_counted_once_per_marker()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], []);
        var handler = Handler(dir, graph, T("XCommand", "Application", "ICommand", "ICommand", "IRequest"));
        var withRequest = new ProjectProfile(Vocabulary: new Dictionary<string, RoleVocabulary> { ["Command"] = new(["ICommand", "IRequest"], [], []) });

        var pv = handler.Handle(new PreviewQuery(dir, withRequest))!;

        Assert.Equal(1, pv.MarkerCounts["ICommand"]);                     // повтор базового типа не удваивает счётчик
        Assert.Equal(1, pv.MarkerCounts["IRequest"]);
    }

    [Fact]
    public void Cancelled_request_does_not_start_a_preview()
    {
        var dir = TempDir();
        var graph = new SystemGraph([Svc("svc-a/app")], [], []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Handler(dir, graph).Handle(new PreviewQuery(dir, ProjectProfile.Default), cts.Token));
    }
}

