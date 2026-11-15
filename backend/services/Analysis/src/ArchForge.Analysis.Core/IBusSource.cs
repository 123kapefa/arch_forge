namespace ArchForge.Analysis.Core;

// Порт: направление обмена сообщениями в коде сервиса (кто публикует, кто потребляет). Чисто СИНТАКСИЧЕСКИЙ разбор;
// реализация — в Infrastructure.Roslyn. Питает рёбра publishes/subscribes и детектор dangling-event / orphaned-subscription:
// compose даёт лишь «uses-bus» (подключён к шине, направление неизвестно, adr/0008).
public interface IBusSource
{
    IReadOnlyList<BusSite> Read(string root, CancellationToken ct = default);
}

public enum BusDirection { Publish, Subscribe, Unknown }

// Основание вывода — от него зависит уверенность: Marker (интерфейс/атрибут потребителя, явный тип публикации) — высокая,
// String (строковый топик/очередь) — средняя, Convention (метод Handle по имени, Wolverine) и Unresolved — низкая;
// Truncated — скан усечён капом (файлов или мест): сайт Unknown, шина по сервису неполная.
public enum BusBasis { Marker, String, Convention, Unresolved, Truncated }

/// <param name="Direction">Publish/Subscribe; Unknown — в коде есть клиент шины, но направление не распознано (шина по такому сервису неполная).</param>
/// <param name="Name">Тип события или строковый топик/очередь; пусто — не определено.</param>
/// <param name="Confidence">0..1 по основанию вывода.</param>
public sealed record BusSite(BusDirection Direction, string Name, string File, int Line, BusBasis Basis, double Confidence);
