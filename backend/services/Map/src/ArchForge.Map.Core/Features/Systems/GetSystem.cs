using ArchForge.Analysis.Contracts;
using ArchForge.Contracts;
using ArchForge.Map.Core.Profiles;
using ArchForge.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchForge.Map.Core.Features.Systems;

/// <summary>
/// GET /api/system?path=&amp;drift=&amp;layers= — системная карта (мульти-сервис, Фаза 1.5): системный IR у Analysis.
/// Map — единственная фронт-фейсинг точка (adr/0010): код-линзы (дрейф/слои) доступны фронту только отсюда.
/// Проекции по сервису/уровню — позже (P1.5-G).
/// </summary>
public sealed class GetSystemEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system", async (string path, bool? drift, bool? layers, string? env, bool? calls, AnalysisClient analysis, ProfileService profiles, CancellationToken ct) =>
        {
            // Профиль проекта (adr/0012): есть привязка — Analysis применяет её; нет — прежнее поведение (профиль по умолчанию).
            ProfileDto? profile = await profiles.EffectiveForAsync(path, ct);
            GraphDto? graph = profile is null
                ? await analysis.GetSystemAsync(path, drift ?? false, layers ?? false, env, calls ?? false, ct)
                : await analysis.GetSystemWithProfileAsync(path, profile, drift ?? false, layers ?? false, env, calls ?? false, ct);
            if (graph is null && profile is not null)
            {
                // Сохранённый профиль мог устареть (правило убрали из каталога) — карта не должна пропадать совсем:
                // показываем разбор по умолчанию и честно говорим, что профиль не применён.
                graph = await analysis.GetSystemAsync(path, drift ?? false, layers ?? false, env, calls ?? false, ct);
                if (graph is not null)
                    graph = graph with { Degradations = [.. graph.Degradations ?? [], "профиль проекта не применён: Analysis его не принял — показан разбор по умолчанию"] };
            }
            return graph is null
                ? Results.BadRequest(new { error = "Analysis не смог собрать систему" })
                : Results.Ok(graph);
        });
}
