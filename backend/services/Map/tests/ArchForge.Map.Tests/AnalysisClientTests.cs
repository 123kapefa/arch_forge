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

    // Ревью #22 High #1: линза «дрейф» достижима через Map — флаг доходит до Analysis query-string.
    [Fact]
    public async Task GetSystemAsync_passes_drift_flag()
    {
        var cap = new CapturingHandler();
        var client = new AnalysisClient(new HttpClient(cap) { BaseAddress = new Uri("http://analysis") });

        await client.GetSystemAsync("/root", drift: true);
        Assert.Contains("drift=true", cap.LastUrl);

        await client.GetSystemAsync("/root", drift: false);
        Assert.DoesNotContain("drift", cap.LastUrl!); // по умолчанию — лёгкий скан, без флага
    }
}
