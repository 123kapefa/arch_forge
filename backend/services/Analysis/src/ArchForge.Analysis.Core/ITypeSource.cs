namespace ArchForge.Analysis.Core;

// Порт: источник сырых типов по пути к сервису. Реализация — Roslyn в Infrastructure.
// ct прокидывается до горячих циклов разбора: abort на клиенте реально останавливает работу (P1-H).
public interface ITypeSource
{
    IReadOnlyList<RawType> Read(string path, CancellationToken ct = default);
}
