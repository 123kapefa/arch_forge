using ArchForge.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Core;

public static class Registration
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton(ConventionPack.Default());
        services.AddSingleton<TypeClassifier>();
        services.AddTransient<Features.Analysis.AnalyzeSolutionHandler>();
        services.AddTransient<Features.Imports.GetImportsHandler>();
        services.AddEndpoints(typeof(Registration).Assembly);
        return services;
    }
}
