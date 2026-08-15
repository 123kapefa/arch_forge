namespace ArchForge.Analysis.Core.Systems;

// Namespace сервиса = префикс id его узлов ({system}/{svc}). Единый источник формулы, чтобы
// location проблем (ServiceAnalyzer → детекторы дрейфа/слоёв) и построение id узлов
// (ComposeSystemSource) не разъехались.
public static class SystemNamespace
{
    public static string Of(string root, string serviceRoot) =>
        Path.GetRelativePath(root, serviceRoot).Replace('\\', '/');
}
