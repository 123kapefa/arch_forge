using ArchForge.Analysis.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

public static class Registration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // Семантика поверх синтаксического пола (adr/0008): ITypeSource = семантика, floor — фолбэк.
        // CreateInstance, а не new: контейнер сам подставит ILogger — иначе лог деградации был бы NullLogger.
        services.AddSingleton<RoslynTypeSource>();
        // Дорогие источники обёрнуты кешем по отпечатку дерева (предпросмотр влияния профиля не читает код заново).
        services.AddSingleton<ScanCache>();
        services.AddSingleton<ITypeSource>(sp => new CachingTypeSource(
            ActivatorUtilities.CreateInstance<RoslynSemanticTypeSource>(sp, sp.GetRequiredService<RoslynTypeSource>()),
            sp.GetRequiredService<ScanCache>()));
        services.AddSingleton<IImportSource, CsprojImportSource>();
        services.AddSingleton<IConstructionSource>(sp => new CachingConstructionSource(new RoslynConstructionSource(), sp.GetRequiredService<ScanCache>()));
        services.AddSingleton<IEndpointSource>(sp => new CachingEndpointSource(new RoslynMinimalApiSource(), sp.GetRequiredService<ScanCache>()));
        services.AddSingleton<IHttpCallSource>(sp => new CachingHttpCallSource(new RoslynHttpCallSource(), sp.GetRequiredService<ScanCache>()));
        return services;
    }
}
