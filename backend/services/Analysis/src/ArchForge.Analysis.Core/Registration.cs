using ArchForge.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Core;

public static class Registration
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton(ConventionPack.Default());
        services.AddSingleton<TypeClassifier>();
        // Код-линзы (дрейф/слои): общий ServiceAnalyzer нужен ITypeSource (Infra) + IServiceDiscoverySource (Manifests).
        services.AddTransient<Systems.ServiceAnalyzer>();
        services.AddTransient<Systems.DriftScanner>();
        services.AddTransient<Systems.LayerViolationScanner>();
        services.AddTransient<Features.Analysis.AnalyzeSolutionHandler>();
        services.AddTransient<Features.Imports.GetImportsHandler>();
        services.AddTransient<Features.Systems.GetSystemHandler>();
        services.AddEndpoints(typeof(Registration).Assembly);
        return services;
    }
}
