namespace ArchForge.Analysis.Core;

// Порт: источник сырых типов по пути к сервису. Реализация — Roslyn в Infrastructure.
public interface ITypeSource
{
    IReadOnlyList<RawType> Read(string path);
}
