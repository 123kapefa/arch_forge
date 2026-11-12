using ArchForge.Map.Core;
using ArchForge.Map.Infrastructure;
using ArchForge.SharedKernel;
using ArchForge.SharedKernel.Auth;

var builder = WebApplication.CreateBuilder(args);

// Шов «путь → IR»: где живёт сервис Analysis (переопределяется конфигом Analysis:BaseUrl).
string analysisUrl = builder.Configuration["Analysis:BaseUrl"] ?? "http://localhost:5100";
builder.Services.AddCore(analysisUrl, AuthExtensions.ValidateInternalKey(builder.Configuration, builder.Environment));
// Вход (adr/0013): проверка токена auth_service либо режим разработки (флаг + Development + loopback); иначе сервис не стартует.
builder.Services.AddArchForgeAuth(builder.Configuration, builder.Environment);
// Хранилище профилей проекта (adr/0012): папка переопределяется конфигом Profiles:Directory.
builder.Services.AddInfrastructure(builder.Configuration["Profiles:Directory"]);
// Тела запросов (профиль, привязка) малы: ограничиваем, чтобы не принимать произвольно большие JSON.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024);

var app = builder.Build();

// Защита локального API от чужих страниц в браузере (CSRF); от DNS rebinding защищает AllowedHosts в appsettings.
app.UseCrossSiteGuard();
app.UseArchForgeAuth();
app.MapEndpoints();
await app.RunAsync();
