using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.SharedKernel;

public static class EndpointExtensions
{
    // Собрать все реализации IEndpoint из сборки в DI.
    public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
            if (type is { IsAbstract: false, IsInterface: false } && typeof(IEndpoint).IsAssignableFrom(type))
                services.AddSingleton(typeof(IEndpoint), type);
        return services;
    }

    // Замапить все зарегистрированные эндпоинты.
    public static WebApplication MapEndpoints(this WebApplication app)
    {
        foreach (var endpoint in app.Services.GetRequiredService<IEnumerable<IEndpoint>>())
            endpoint.MapEndpoint(app);
        return app;
    }
}
