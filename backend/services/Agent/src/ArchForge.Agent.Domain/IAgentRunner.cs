namespace ArchForge.Agent.Domain;

// Порт: запуск ИИ-провайдера, правящего код в рабочей папке. Реализации-адаптеры — в Infrastructure
// (Claude CLI / Codex / локальная модель / кастомный шаблон, adr/0005). Приложение агностично к подписке:
// авторизацию держит CLI пользователя (BYO). Наш код целевой код наружу не шлёт.
public interface IAgentRunner
{
    /// <summary>Идентификатор провайдера: "claude" | "codex" | "local" | "custom".</summary>
    string ProviderId { get; }

    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default);
}
