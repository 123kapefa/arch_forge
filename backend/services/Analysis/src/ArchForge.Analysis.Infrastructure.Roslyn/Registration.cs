using ArchForge.Analysis.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<IScanCacheControl>(sp => Cache(sp));   // «Пере-анализировать» сбрасывает кеш
        // Лог пропуска слишком глубоких файлов (#167): синтаксические источники создаются без DI, поэтому лог общий, ставим его здесь.
        services.AddSingleton<ITypeSource>(sp => new CachingTypeSource(
            ActivatorUtilities.CreateInstance<RoslynSemanticTypeSource>(sp, sp.GetRequiredService<RoslynTypeSource>()),
            Cache(sp)));
        services.AddSingleton<IImportSource, CsprojImportSource>();
        services.AddSingleton<IConstructionSource>(sp => new CachingConstructionSource(new RoslynConstructionSource(), Cache(sp)));
        services.AddSingleton<IEndpointSource>(sp => new CachingEndpointSource(new RoslynMinimalApiSource(), Cache(sp)));
        services.AddSingleton<IHttpCallSource>(sp => new CachingHttpCallSource(new RoslynHttpCallSource(), Cache(sp)));
        services.AddSingleton<IGuardSource>(sp => new CachingGuardSource(new RoslynGuardSource(), Cache(sp)));   // проверки на null (redundant-null-check)
        services.AddSingleton<IErrorStrategySource>(sp => new CachingErrorStrategySource(new RoslynErrorStrategySource(), Cache(sp)));   // стратегия ошибок (mixed-error-strategy)
        services.AddSingleton<IClientLibraryResolver, CsprojClientLibraryResolver>();   // клиентские библиотеки сервиса (*.Contracts), E3.1
        return services;
    }

    // Любой кеширующий источник создаётся через это: так лог пропуска глубоких файлов (#167) подключён, какой бы источник ни встретил файл первым.
    private static ScanCache Cache(IServiceProvider sp)
    {
        RoslynScan.Log = sp.GetService<ILoggerFactory>()?.CreateLogger("ArchForge.Analysis.Roslyn.Scan") ?? RoslynScan.Log;
        return sp.GetRequiredService<ScanCache>();
    }
}
