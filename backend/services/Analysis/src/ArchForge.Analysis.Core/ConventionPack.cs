namespace ArchForge.Analysis.Core;

// Convention-pack: маркер (базовый тип/интерфейс) → роль. Источник — свод команды.
public sealed class ConventionPack(
    IReadOnlyDictionary<string, string> markers,
    IReadOnlyDictionary<string, string> fallback,
    IReadOnlyList<(string Suffix, string Marker, string Role)> expect)
{
    public IReadOnlyDictionary<string, string> Markers { get; } = markers;
    public IReadOnlyDictionary<string, string> Fallback { get; } = fallback;
    public IReadOnlyList<(string Suffix, string Marker, string Role)> Expect { get; } = expect;

    // Дефолт: маркеры свода + популярные .NET-паттерны. Пользовательский пак заменит/дополнит.
    public static ConventionPack Default() => new(
        markers: new Dictionary<string, string>
        {
            ["ICommand"] = "Command", ["IQuery"] = "Query",
            ["ICommandHandler"] = "Handler", ["IQueryHandler"] = "Handler", ["IQueryHandlerWithResult"] = "Handler",
            ["AbstractValidator"] = "Validator", ["IEndpoint"] = "Endpoint",
            ["Aggregate"] = "Aggregate", ["IDomainEvent"] = "DomainEvent",
        },
        fallback: new Dictionary<string, string>
        {
            ["IdentityUser"] = "Entity (Identity)", ["IdentityRole"] = "Role (Identity)",
            ["IdentityDbContext"] = "DbContext", ["DbContext"] = "DbContext",
            ["PageModel"] = "Razor Page", ["ControllerBase"] = "Controller",
            ["Migration"] = "EF migration", ["ModelSnapshot"] = "EF snapshot",
            ["IEntityTypeConfiguration"] = "EF config", ["IDesignTimeDbContextFactory"] = "EF factory",
            ["IHostedService"] = "Hosted service", ["BackgroundService"] = "Hosted service",
            ["IOpenIddictServerHandler"] = "OIDC handler",
            ["OpenIddictEntityFrameworkCoreApplication"] = "OIDC entity",
            ["OpenIddictEntityFrameworkCoreAuthorization"] = "OIDC entity",
            ["OpenIddictEntityFrameworkCoreScope"] = "OIDC entity",
            ["OpenIddictEntityFrameworkCoreToken"] = "OIDC entity",
            ["Exception"] = "Exception", ["Attribute"] = "Attribute",
        },
        expect: new (string, string, string)[]
        {
            ("Command", "ICommand", "Command"),
            ("Handler", "ICommandHandler/IQueryHandler", "Handler"),
            ("Validator", "AbstractValidator", "Validator"),
            ("Endpoint", "IEndpoint", "Endpoint"),
        });
}
