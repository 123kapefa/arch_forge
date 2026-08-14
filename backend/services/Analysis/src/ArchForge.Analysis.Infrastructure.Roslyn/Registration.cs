using ArchForge.Analysis.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

public static class Registration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ITypeSource, RoslynTypeSource>();
        return services;
    }
}
