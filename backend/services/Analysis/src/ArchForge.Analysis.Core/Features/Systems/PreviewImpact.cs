using ArchForge.Analysis.Core.Profiles;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Contracts;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ArchForge.Analysis.Core.Features.Systems;

// Предпросмотр влияния профиля (adr/0012): «что изменится в находках, если выбрать такой профиль».
// Не меняет ничего и не сохраняется. Дорогой разбор кода берётся из кеша источников (ScanCache), поэтому
// после первого вызова каждая правка стоит лишь детекторов и классификации.

public sealed record PreviewQuery(string Path, ProjectProfile Profile);

public sealed class PreviewImpactHandler(GetSystemHandler system, ISystemSource source, ServiceAnalyzer? analyzer = null)
{
    // Предпросмотр тяжёл на холодном кеше (разбор кода). Одновременно считаем не больше двух: быстрые правки и чужие
    // запросы не должны занимать все потоки. Ожидание в очереди прерывается отменой клиента.
    private static readonly SemaphoreSlim Gate = new(2, 2);

    public PreviewDto? Handle(PreviewQuery query, CancellationToken ct = default)
    {
        var path = query.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;

        Gate.Wait(ct);
        try { return Compute(query, path, ct); }
        finally { Gate.Release(); }
    }

    private PreviewDto? Compute(PreviewQuery query, string path, CancellationToken ct)
    {
        // Обе линзы кода включены: предпросмотр должен показать всё, что профиль может изменить.
        var current = system.Handle(new GetSystemQuery(path, Drift: true, Layers: true, Profile: query.Profile), ct);
        var baselineProfile = new ProjectProfile(query.Profile.Level);        // тот же уровень, без отличий
        var baseline = system.Handle(new GetSystemQuery(path, Drift: true, Layers: true, Profile: baselineProfile), ct);
        if (current is null || baseline is null) return null;

        // Данные для подсказки уровня и счётчиков словаря.
        var graph = source.Read(path, null, ct);
        var pack = ConventionPack.Default().WithProfile(query.Profile);
        var analyses = analyzer?.Analyze(path, pack, ct) ?? [];
        var types = analyses.Where(a => a.Result is not null).SelectMany(a => a.Result!.Types)
            .Where(t => t.Layer is not ("Shared" or "tests"))                  // как в классификаторе: свод — про сервисный код
            .ToList();

        // Один проход по типам: маркеры — пересечение базовых типов с набором, суффиксы — по длине (число разных длин мало),
        // а не «каждый маркер/суффикс × каждый тип» (профиль может нести тысячи записей).
        var markerCounts = pack.Markers.Keys.Concat(pack.Fallback.Keys).Distinct(StringComparer.Ordinal).ToDictionary(m => m, _ => 0, StringComparer.Ordinal);
        var suffixCounts = pack.Expect.Select(e => e.Suffix).Concat(query.Profile.Vocabulary?.Values.SelectMany(v => v.Suffixes) ?? [])
            .Distinct(StringComparer.Ordinal).ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        var bySuffixLength = suffixCounts.Keys.Where(k => k.Length > 0).GroupBy(k => k.Length).ToDictionary(g => g.Key, g => g.ToHashSet(StringComparer.Ordinal));
        foreach (var t in types)
        {
            foreach (var b in t.Bases.Distinct(StringComparer.Ordinal))
                if (markerCounts.TryGetValue(b, out var n)) markerCounts[b] = n + 1;
            foreach (var (len, set) in bySuffixLength)
                if (t.Name.Length >= len && set.Contains(t.Name[^len..])) suffixCounts[t.Name[^len..]]++;
        }

        var typeCount = analyses.Where(a => a.Result is not null).Sum(a => a.Result!.Coverage.Marker + a.Result.Coverage.Heuristic + a.Result.Coverage.Unknown);
        var marked = analyses.Where(a => a.Result is not null).Sum(a => a.Result!.Coverage.Marker);
        var hint = LevelHint.Suggest(
            graph.Nodes.Count(n => n.Kind == SystemNodeKind.Service),
            graph.Nodes.Any(n => n.Kind == SystemNodeKind.Topic),
            typeCount > 0 ? marked * 100 / typeCount : 0,
            typeCount);

        return new PreviewDto(Summarize(current, query.Profile), Summarize(baseline, baselineProfile), hint, markerCounts, suffixCounts);
    }

    private static bool IsEnabled(ProjectProfile profile, string kind) =>
        kind.StartsWith(CustomRuleTemplates.KindPrefix, StringComparison.Ordinal)
            ? !(profile.CustomRules ?? []).Any(r => CustomRuleTemplates.KindPrefix + r.Id == kind && r.Strictness.Equals("Off", StringComparison.OrdinalIgnoreCase))
            : profile.Effective(kind) != Strictness.Off;

    /// <summary>Находки по строгости и по правилам. Находка — отдельный факт агрегированной проблемы (тип, пара слоёв…),
    /// а у проблемы без перечня — она сама. Покрытие и служебные отметки не считаются.</summary>
    internal static ImpactSummaryDto Summarize(GraphDto dto, ProjectProfile profile)
    {
        var counts = RuleCatalog.All.ToDictionary(r => r.Id, _ => 0, StringComparer.Ordinal);
        foreach (var r in profile.CustomRules ?? []) counts[CustomRuleTemplates.KindPrefix + r.Id] = 0;
        int errors = 0, warnings = 0, infos = 0;
        foreach (var p in dto.Problems ?? [])
        {
            if (!counts.ContainsKey(p.Kind)) continue;
            if (p.Severity is not ("critical" or "warning" or "info")) continue;   // покрытие и служебные отметки — не находки, сумма по правилам = сумме по строгости
            var n = p.Items is { Count: > 0 } ? p.Items.Count : 1;
            counts[p.Kind] += n;
            switch (p.Severity)
            {
                case "critical": errors += n; break;
                case "warning": warnings += n; break;
                case "info": infos += n; break;
            }
        }
        var rules = counts.ToDictionary(kv => kv.Key,
            kv => new RuleImpactDto(kv.Value, IsEnabled(profile, kv.Key)), StringComparer.Ordinal);
        return new ImpactSummaryDto(errors, warnings, infos, rules);
    }
}

/// <summary>POST /api/system/preview?path=… — предпросмотр влияния профиля (тело — ProfileDto).
/// Неверный профиль — 400 со списком ошибок; анализ при этом не запускается.</summary>
public sealed class PreviewImpactEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/api/system/preview", (string path, ProfileDto? profile, PreviewImpactHandler handler,
            ILogger<PreviewImpactEndpoint> log, CancellationToken ct) =>
        {
            if (!ProfileMapping.TryToProfile(profile, out var parsed, out var errors))
                return Results.BadRequest(new { error = "профиль не принят", errors });
            try
            {
                var dto = handler.Handle(new PreviewQuery(path, parsed), ct);
                return dto is null
                    ? Results.BadRequest(new { error = "path обязателен и должен быть существующей папкой" })
                    : Results.Ok(dto);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                log.LogInformation("Предпросмотр влияния отменён клиентом (abort): {Path}", path);
                throw;
            }
        });
}
