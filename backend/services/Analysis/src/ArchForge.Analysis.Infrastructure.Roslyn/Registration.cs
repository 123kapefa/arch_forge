using ArchForge.Analysis.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

public static class Registration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // Семантика поверх синтаксического пола (adr/0008): ITypeSource = семантика, floor — фолбэк.
        services.AddSingleton<RoslynTypeSource>();
        services.AddSingleton<ITypeSource>(sp =>
            new RoslynSemanticTypeSource(sp.GetRequiredService<RoslynTypeSource>()));
        services.AddSingleton<IImportSource, CsprojImportSource>();
        return services;
    }
}
