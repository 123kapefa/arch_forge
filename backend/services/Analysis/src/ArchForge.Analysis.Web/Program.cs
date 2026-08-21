using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Manifests;
using ArchForge.Analysis.Infrastructure.Roslyn;
using ArchForge.SharedKernel;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCore();
builder.Services.AddInfrastructure();  // Roslyn: ITypeSource/IImportSource
builder.Services.AddManifests();       // Manifests: ISystemSource (мульти-сервис, Фаза 1.5)

var app = builder.Build();
app.MapEndpoints();
await app.RunAsync();
