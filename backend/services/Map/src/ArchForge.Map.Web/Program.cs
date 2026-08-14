using ArchForge.Map.Core;
using ArchForge.SharedKernel;

var builder = WebApplication.CreateBuilder(args);

// Шов «путь → IR»: где живёт сервис Analysis (переопределяется конфигом Analysis:BaseUrl).
string analysisUrl = builder.Configuration["Analysis:BaseUrl"] ?? "http://localhost:5100";
builder.Services.AddCore(analysisUrl);

var app = builder.Build();
app.MapEndpoints();
await app.RunAsync();
