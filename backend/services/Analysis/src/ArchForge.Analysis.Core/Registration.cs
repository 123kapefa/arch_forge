using ArchForge.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Analysis.Core;

public static class Registration
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton<TypeClassifier>(); // без состояния: пак приходит в Classify (свой у проекта)
        // Код-линзы (дрейф/слои): один общий ServiceAnalyzer (нужны ITypeSource из Infra + IServiceDiscoverySource
        // из Manifests); хендлер гоняет его раз и питает чистые детекторы DriftDetector/LayerViolationDetector.
        services.AddTransient<Systems.ServiceAnalyzer>();
        services.AddTransient<Features.Analysis.AnalyzeSolutionHandler>();
        services.AddTransient<Features.Imports.GetImportsHandler>();
        services.AddTransient<Features.Systems.GetSystemHandler>();
        services.AddEndpoints(typeof(Registration).Assembly);
        return services;
    }
}
