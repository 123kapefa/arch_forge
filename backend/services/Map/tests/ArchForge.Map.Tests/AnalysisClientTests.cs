using System.Net;
using System.Net.Http.Json;
using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;

namespace ArchForge.Map.Tests;

// Шов Map↔Analysis: AnalysisClient поверх стаба транспорта (без живого Analysis).
public class AnalysisClientTests
{
    private sealed class StubHandler(HttpStatusCode code, object? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(code);
            if (body is not null) resp.Content = JsonContent.Create(body);
            return Task.FromResult(resp);
        }
    }

    private static AnalysisClient Client(HttpStatusCode code, object? body) =>
        new(new HttpClient(new StubHandler(code, body)) { BaseAddress = new Uri("http://analysis") });

    // Захват URL запроса — для проверки проброса query-параметров.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GraphDto("sys", [], [], new CoverageDto(0, 0, 0, 0), [], Health: "ok")),
            });
        }
    }

    [Fact]
    public async Task GetSystemAsync_returns_graph_on_success()
    {
        var graph = new GraphDto("sys", [], [], new CoverageDto(0, 0, 0, 0), [], Health: "ok");

        var g = await Client(HttpStatusCode.OK, graph).GetSystemAsync("/root");

        Assert.NotNull(g);
        Assert.Equal("sys", g!.Project);
    }

    [Fact]
    public async Task GetSystemAsync_null_on_error_status()
    {
        var g = await Client(HttpStatusCode.BadRequest, null).GetSystemAsync("/root");

        Assert.Null(g);
    }

    // Код-линзы (дрейф/слои) достижимы фронту через Map — флаги доходят до Analysis query-string.
    [Fact]
    public async Task GetSystemAsync_passes_code_lens_flags()
    {
        var cap = new CapturingHandler();
        var client = new AnalysisClient(new HttpClient(cap) { BaseAddress = new Uri("http://analysis") });

        await client.GetSystemAsync("/root", drift: true);
        Assert.Contains("drift=true", cap.LastUrl);

        await client.GetSystemAsync("/root", layers: true);
        Assert.Contains("layers=true", cap.LastUrl);

        await client.GetSystemAsync("/root");
        Assert.DoesNotContain("drift", cap.LastUrl!);   // по умолчанию — лёгкий скан, без флагов
        Assert.DoesNotContain("layers", cap.LastUrl!);
    }

    // E3.1: флаг call-graph доходит до Analysis query-string; по умолчанию — без него.
    [Fact]
    public async Task GetSystemAsync_passes_calls_flag()
    {
        var cap = new CapturingHandler();
        var client = new AnalysisClient(new HttpClient(cap) { BaseAddress = new Uri("http://analysis") });

        await client.GetSystemAsync("/root", calls: true);
        Assert.Contains("calls=true", cap.LastUrl);

        await client.GetSystemAsync("/root");
        Assert.DoesNotContain("calls", cap.LastUrl!);
    }

    // E9.1 слой 2: выбранная среда доходит до Analysis query-string; по умолчанию — без env.
    [Fact]
    public async Task GetSystemAsync_passes_env()
    {
        var cap = new CapturingHandler();
        var client = new AnalysisClient(new HttpClient(cap) { BaseAddress = new Uri("http://analysis") });

        await client.GetSystemAsync("/root", env: "prod");
        Assert.Contains("env=prod", cap.LastUrl);

        await client.GetSystemAsync("/root");
        Assert.DoesNotContain("env=", cap.LastUrl!);
    }

    // ── профиль (adr/0012) ──

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Url = request.RequestUri?.AbsoluteUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GraphDto("sys", [], [], new CoverageDto(0, 0, 0, 0), [], Health: "ok")),
            };
        }
    }

    [Fact]
    public async Task GetSystemWithProfileAsync_posts_profile_body_and_keeps_flags()
    {
        var rec = new RecordingHandler();
        var client = new AnalysisClient(new HttpClient(rec) { BaseAddress = new Uri("http://analysis") });
        var profile = new ProfileDto("Simple", new Dictionary<string, string> { ["drift"] = "Warning" });

        var g = await client.GetSystemWithProfileAsync("/root x", profile, drift: true, env: "stage");

        Assert.Equal("sys", g!.Project);
        Assert.Equal(HttpMethod.Post, rec.Method);
        Assert.Contains("/api/system?path=%2Froot%20x", rec.Url);              // путь экранирован
        Assert.Contains("drift=true", rec.Url);
        Assert.Contains("env=stage", rec.Url);
        Assert.Contains("\"level\":\"Simple\"", rec.Body);
        Assert.Contains("Warning", rec.Body);
    }

    [Fact]
    public async Task GetSystemWithProfileAsync_null_when_analysis_rejects_profile()
    {
        var g = await Client(HttpStatusCode.BadRequest, null).GetSystemWithProfileAsync("/root", new ProfileDto("Nope"));

        Assert.Null(g);
    }

    [Fact]
    public async Task GetRulesAsync_reads_catalog_and_null_on_error()
    {
        var dto = new RuleCatalogDto("2026.11", [new RuleDto("drift", "convention", "t", "n", new Dictionary<string, string?> { ["Simple"] = "Off" })]);

        var ok = await Client(HttpStatusCode.OK, dto).GetRulesAsync();
        var bad = await Client(HttpStatusCode.InternalServerError, null).GetRulesAsync();

        Assert.Equal("2026.11", ok!.Version);
        Assert.Equal("Off", ok.Rules[0].Defaults["Simple"]);
        Assert.Null(bad);
    }

    [Fact]
    public async Task PreviewImpactAsync_posts_profile_and_reads_result()
    {
        var rec = new RecordingHandler();
        var none = new Dictionary<string, RuleImpactDto>();
        var dto = new PreviewDto(new ImpactSummaryDto(1, 0, 0, none), new ImpactSummaryDto(2, 0, 0, none), new LevelHintDto("Simple", ["один сервис"]), new Dictionary<string, int>(), new Dictionary<string, int>());

        var ok = await Client(HttpStatusCode.OK, dto).PreviewImpactAsync("/root", new ProfileDto("Standard"));
        var bad = await Client(HttpStatusCode.BadRequest, null).PreviewImpactAsync("/root", new ProfileDto("Standard"));
        var client = new AnalysisClient(new HttpClient(rec) { BaseAddress = new Uri("http://analysis") });
        await client.PreviewImpactAsync("/root x", new ProfileDto("Simple"));

        Assert.Equal(1, ok!.Current.Errors);
        Assert.Equal("Simple", ok.Hint.Level);
        Assert.Null(bad);
        Assert.Equal(HttpMethod.Post, rec.Method);
        Assert.Contains("/api/system/preview?path=%2Froot%20x", rec.Url);
    }

    [Fact]
    public async Task PreviewImpactAsync_cancellation_by_caller_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AnalysisClient(new HttpClient(new RecordingHandler()) { BaseAddress = new Uri("http://analysis") })
                .PreviewImpactAsync("/root", new ProfileDto("Standard"), cts.Token));
    }

    [Fact]
    public async Task Refresh_flag_reaches_analysis_on_both_system_calls()
    {
        var rec = new RecordingHandler();
        var client = new AnalysisClient(new HttpClient(rec) { BaseAddress = new Uri("http://analysis") });

        await client.GetSystemAsync("/root", refresh: true);
        Assert.Contains("refresh=true", rec.Url);

        await client.GetSystemWithProfileAsync("/root", new ProfileDto("Standard"), refresh: true);
        Assert.Contains("refresh=true", rec.Url);

        await client.GetSystemAsync("/root");
        Assert.DoesNotContain("refresh", rec.Url!);                       // по умолчанию кеш не сбрасывается
    }
}

