using ArchForge.Map.Core;
using ArchForge.Map.Infrastructure;
using ArchForge.SharedKernel;

var builder = WebApplication.CreateBuilder(args);

// Шов «путь → IR»: где живёт сервис Analysis (переопределяется конфигом Analysis:BaseUrl).
string analysisUrl = builder.Configuration["Analysis:BaseUrl"] ?? "http://localhost:5100";
builder.Services.AddCore(analysisUrl);
// Хранилище профилей проекта (adr/0012): папка переопределяется конфигом Profiles:Directory.
builder.Services.AddInfrastructure(builder.Configuration["Profiles:Directory"]);
// Тела запросов (профиль, привязка) малы: ограничиваем, чтобы не принимать произвольно большие JSON.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024);

var app = builder.Build();
app.MapEndpoints();
await app.RunAsync();
