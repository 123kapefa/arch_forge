namespace ArchForge.Analysis.Core;

// Порт: DI-контейнер сервиса (E4.2) — регистрации времён жизни + зависимости из конструкторов.
// Чисто СИНТАКСИЧЕСКИЙ разбор (adr/0008: «пол»): имена типов простые, без пространств имён и арности дженериков.
// Реализация — Roslyn в Infrastructure; проверку времён жизни делает чистый детектор (Systems/DiLifetimeDetector).
public interface IDiSource
{
    DiModel Read(string root, CancellationToken ct = default);
}

public enum DiLifetime { Singleton, Scoped, Transient }

/// <summary>Одна регистрация. <paramref name="Service"/> — под каким типом резолвится, <paramref name="Impl"/> — какой класс создаётся
/// (null — неизвестен: фабрика <c>sp =&gt; …</c> без явного типа). <paramref name="FactoryDeps"/> — типы из <c>GetService&lt;T&gt;()</c> в теле фабрики.
/// <paramref name="TryAdd"/> — <c>TryAdd*</c>: действует, только если сервис не зарегистрирован иначе. <paramref name="Keyed"/> — регистрация по ключу (`AddKeyed*`): с обычными не смешивается.</summary>
public sealed record DiRegistration(
    string Service, string? Impl, DiLifetime Lifetime, string File, int Line,
    bool Factory = false, bool TryAdd = false, IReadOnlyList<string>? FactoryDeps = null, bool Keyed = false);

/// <summary>Зависимость конструктора: простое имя типа; <paramref name="Required"/> = false — коллекция (<c>IEnumerable&lt;T&gt;</c>, пустая допустима),
/// nullable или параметр со значением по умолчанию: такой тип может быть не зарегистрирован, но времена жизни его всё равно касаются.</summary>
public sealed record DiDep(string Type, bool Required = true);

/// <summary>Класс из таргета с зависимостями основного конструктора (primary ctor или самый «жадный» не-private конструктор).
/// <paramref name="Kind"/>: interface | class (конкретный, не abstract/static) | other (record/struct/enum/abstract/static — в контейнере не резолвится).
/// <paramref name="AmbiguousCtor"/> — несколько конструкторов с параметрами без `[ActivatorUtilitiesConstructor]`: контейнер может выбрать не тот, что мы прочли.
/// <paramref name="Resolvable"/> — класс подставляется фреймворком без регистрации (контроллер).</summary>
public sealed record DiClass(string Name, string Kind, IReadOnlyList<DiDep> CtorDeps, string File, int Line, bool Resolvable = false, bool AmbiguousCtor = false);

/// <summary>Результат разбора DI сервиса. <paramref name="OpaqueReason"/> — регистрация идёт сканированием сборок/динамически
/// (Scrutor, MediatR, <c>AddScoped(type)</c>…): набор регистраций неполон, «не зарегистрировано» — только с низкой уверенностью.</summary>
public sealed record DiModel(
    IReadOnlyList<DiRegistration> Registrations, IReadOnlyList<DiClass> Classes, string? OpaqueReason = null)
{
    public static readonly DiModel Empty = new([], []);
}
