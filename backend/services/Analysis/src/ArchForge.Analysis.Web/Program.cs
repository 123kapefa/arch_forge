using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Manifests;
using ArchForge.Analysis.Infrastructure.Roslyn;
using ArchForge.SharedKernel;
using ArchForge.SharedKernel.Auth;

var builder = WebApplication.CreateBuilder(args);
// Тела запросов (профиль проекта) малы: ограничиваем, чтобы не принимать произвольно большие JSON.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024);
builder.Services.AddCore();
builder.Services.AddInfrastructure();  // Roslyn: ITypeSource/IImportSource
builder.Services.AddManifests();       // Manifests: ISystemSource (мульти-сервис, Фаза 1.5)

var app = builder.Build();
// Analysis внутренний (adr/0013): только вызовы Map с общим ключом и с loopback.
app.UseInternalGuard();
app.MapEndpoints();
await app.RunAsync();
