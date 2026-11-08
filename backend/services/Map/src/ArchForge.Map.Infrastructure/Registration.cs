using ArchForge.Map.Core.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace ArchForge.Map.Infrastructure;

public static class Registration
{
    /// <summary>Регистрирует хранилище профилей (adr/0012). <paramref name="directory"/> = null → папка данных пользователя.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? directory = null)
    {
        services.AddSingleton<IProfileStore>(_ => new FileProfileStore(directory ?? FileProfileStore.DefaultDirectory()));
        return services;
    }
}
