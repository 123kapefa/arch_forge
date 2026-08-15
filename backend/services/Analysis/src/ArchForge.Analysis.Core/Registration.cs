using ArchForge.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Core;

public static class Registration
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton(ConventionPack.Default());
        services.AddSingleton<TypeClassifier>();
        services.AddTransient<Systems.DriftScanner>(); // линза «дрейф»: нужны ITypeSource (Infra) + IServiceDiscoverySource (Manifests)
        services.AddTransient<Features.Analysis.AnalyzeSolutionHandler>();
        services.AddTransient<Features.Imports.GetImportsHandler>();
        services.AddTransient<Features.Systems.GetSystemHandler>();
        services.AddEndpoints(typeof(Registration).Assembly);
        return services;
    }
}
