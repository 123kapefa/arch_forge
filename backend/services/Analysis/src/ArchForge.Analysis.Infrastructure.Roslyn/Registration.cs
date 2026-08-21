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
        services.AddSingleton<ITypeSource>(sp =>
            ActivatorUtilities.CreateInstance<RoslynSemanticTypeSource>(sp, sp.GetRequiredService<RoslynTypeSource>()));
        services.AddSingleton<IImportSource, CsprojImportSource>();
        services.AddSingleton<IConstructionSource, RoslynConstructionSource>(); // ось usage-графа (пока источник; потребитель — детектор далее)
        services.AddSingleton<IEndpointSource, RoslynMinimalApiSource>();        // minimal-API эндпоинты для L1 (#71)
        return services;
    }
}
