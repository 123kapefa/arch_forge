using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Roslyn;
using ArchForge.SharedKernel;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCore();
builder.Services.AddInfrastructure();

var app = builder.Build();
app.MapEndpoints();
await app.RunAsync();
