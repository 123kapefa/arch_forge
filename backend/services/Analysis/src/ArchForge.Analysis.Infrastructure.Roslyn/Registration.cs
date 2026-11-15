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
        services.AddSingleton<IScanCacheControl>(sp => sp.GetRequiredService<ScanCache>());   // «Пере-анализировать» сбрасывает кеш
        services.AddSingleton<ITypeSource>(sp => new CachingTypeSource(
            ActivatorUtilities.CreateInstance<RoslynSemanticTypeSource>(sp, sp.GetRequiredService<RoslynTypeSource>()),
            sp.GetRequiredService<ScanCache>()));
        services.AddSingleton<IImportSource, CsprojImportSource>();
        services.AddSingleton<IConstructionSource>(sp => new CachingConstructionSource(new RoslynConstructionSource(), sp.GetRequiredService<ScanCache>()));
        services.AddSingleton<IEndpointSource>(sp => new CachingEndpointSource(new RoslynMinimalApiSource(), sp.GetRequiredService<ScanCache>()));
        services.AddSingleton<IHttpCallSource>(sp => new CachingHttpCallSource(new RoslynHttpCallSource(), sp.GetRequiredService<ScanCache>()));
        services.AddSingleton<IDiSource>(sp => new CachingDiSource(new RoslynDiSource(), sp.GetRequiredService<ScanCache>()));   // DI-контейнер сервиса, E4.2
        services.AddSingleton<IGuardSource>(sp => new CachingGuardSource(new RoslynGuardSource(), sp.GetRequiredService<ScanCache>()));   // проверки на null (redundant-null-check)
        services.AddSingleton<IBusSource>(sp => new CachingBusSource(new RoslynBusSource(), sp.GetRequiredService<ScanCache>()));   // pub/sub шины (#61)
        services.AddSingleton<IConfigKeySource>(sp => new RoslynConfigKeySource(sp.GetRequiredService<ScanCache>()));   // ключи конфигурации (E9.3)
        services.AddSingleton<IFolderSource>(sp => new CachingFolderSource(new RoslynFolderSource(), sp.GetRequiredService<ScanCache>()));   // раскладка (folder-structure)
        services.AddSingleton<IAggregateEventSource>(sp => new CachingAggregateEventSource(new RoslynAggregateEventSource(), sp.GetRequiredService<ScanCache>()));   // события в агрегате (aggregate-events)
        services.AddSingleton<IAggregateTimeSource>(sp => new CachingAggregateTimeSource(new RoslynAggregateTimeSource(), sp.GetRequiredService<ScanCache>()));   // время и аудит (aggregate-time)
        services.AddSingleton<IValidationSource>(sp => new CachingValidationSource(new RoslynValidationSource(), sp.GetRequiredService<ScanCache>()));   // валидация (double-validation)
        services.AddSingleton<IErrorStrategySource>(sp => new CachingErrorStrategySource(new RoslynErrorStrategySource(), sp.GetRequiredService<ScanCache>()));   // стратегия ошибок (mixed-error-strategy)
        services.AddSingleton<IClientLibraryResolver, CsprojClientLibraryResolver>();   // клиентские библиотеки сервиса (*.Contracts), E3.1
        return services;
    }
}
