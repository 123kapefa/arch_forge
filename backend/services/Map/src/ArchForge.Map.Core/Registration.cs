using ArchForge.Analysis.Contracts;
using ArchForge.Map.Core.Profiles;
using ArchForge.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Map.Core;

public static class Registration
{
    /// <summary>Регистрирует ядро Map: типизированный клиент к Analysis и эндпоинты-срезы.</summary>
    /// <param name="analysisBaseUrl">Базовый URL сервиса Analysis (шов «путь → IR»).</param>
    public static IServiceCollection AddCore(this IServiceCollection services, string analysisBaseUrl)
    {
        services.AddHttpClient<AnalysisClient>(client =>
            client.BaseAddress = new Uri(analysisBaseUrl));
        services.AddSingleton<IRuleCatalogProvider, AnalysisRuleCatalogProvider>();
        services.AddSingleton<ProfileService>();
        services.AddEndpoints(typeof(Registration).Assembly);
        return services;
    }
}
