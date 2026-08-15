using ArchForge.Analysis.Core.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Infrastructure.Manifests;

public static class Registration
{
    // Системный источник (мульти-источниковый разбор Фазы 1.5): discovery по .slnx + compose-парсинг.
    public static IServiceCollection AddManifests(this IServiceCollection services)
    {
        services.AddSingleton<IServiceDiscoverySource, FolderDiscoverySource>();
        services.AddSingleton<ISystemSource, ComposeSystemSource>();
        return services;
    }
}
