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
        {
            client.BaseAddress = new Uri(analysisBaseUrl);
            // Холодный разбор большого проекта (предпросмотр профиля, первый /api/system) идёт дольше стандартных 100 секунд.
            // Фронт ставит свои, более короткие сроки и рвёт запрос отменой — она доходит до Analysis.
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IRuleCatalogProvider, AnalysisRuleCatalogProvider>();
        services.AddSingleton<ProfileService>();
        services.AddEndpoints(typeof(Registration).Assembly);
        return services;
    }
}
