using ArchForge.Analysis.Contracts;
using ArchForge.Map.Core.Profiles;
using ArchForge.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Map.Core;

public static class Registration
{
    /// <summary>Регистрирует ядро Map: типизированный клиент к Analysis и эндпоинты-срезы.</summary>
    /// <param name="analysisBaseUrl">Базовый URL сервиса Analysis (шов «путь → IR»).</param>
    /// <param name="internalKey">Общий ключ Map → Analysis (adr/0013): уходит в заголовке каждого вызова. null — без ключа (допустимо только в Development).</param>
    public static IServiceCollection AddCore(this IServiceCollection services, string analysisBaseUrl, string? internalKey = null)
    {
        services.AddTransient(_ => new InternalKeyHandler(internalKey));
        services.AddHttpClient<AnalysisClient>(client =>
        {
            client.BaseAddress = new Uri(analysisBaseUrl);
            // Холодный разбор большого проекта (предпросмотр профиля, первый /api/system) идёт дольше стандартных 100 секунд.
            // Фронт ставит свои, более короткие сроки и рвёт запрос отменой — она доходит до Analysis.
            client.Timeout = TimeSpan.FromMinutes(5);
        }).AddHttpMessageHandler<InternalKeyHandler>();
        services.AddSingleton<IRuleCatalogProvider, AnalysisRuleCatalogProvider>();
        services.AddSingleton<ProfileService>();
        services.AddEndpoints(typeof(Registration).Assembly);
        return services;
    }
}

/// <summary>Добавляет общий ключ к каждому вызову Analysis.</summary>
internal sealed class InternalKeyHandler(string? key) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation(ArchForge.SharedKernel.Auth.AuthExtensions.InternalKeyHeader, key);
        return base.SendAsync(request, ct);
    }
}
