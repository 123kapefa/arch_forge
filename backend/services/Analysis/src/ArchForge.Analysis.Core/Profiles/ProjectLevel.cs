namespace ArchForge.Analysis.Core.Profiles;

/// <summary>Уровень масштаба проекта (adr/0012): задаёт значения правил по умолчанию.
/// Simple — небольшой сервис; Standard — средний проект (значение по умолчанию); Platform — платформа из многих сервисов.</summary>
public enum ProjectLevel { Simple, Standard, Platform }

/// <summary>Строгость правила в профиле. Off — не проверяется (и это видно, а не молчит).</summary>
public enum Strictness { Off, Info, Warning, Error }
