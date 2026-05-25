namespace CodeMemory.Indexing.Architecture;

public sealed record ComponentInfo(
    string Name,
    int FileCount,
    int SymbolCount);

public sealed record ArchitectureOverview(
    IReadOnlyList<ComponentInfo> TopLevelComponents,
    IReadOnlyDictionary<string, int> LanguageBreakdown,
    int TotalFiles,
    int TotalSymbols);

public sealed record ComponentCluster(
    string Name,
    IReadOnlyList<string> Members,
    double CohesionScore);

public interface IArchitectureService
{
    Task<ArchitectureOverview> GetOverviewAsync(
        string? path = null, int depth = 1, CancellationToken ct = default);
}

public interface IComponentClusteringService
{
    Task<IReadOnlyList<ComponentCluster>> GetClustersAsync(
        double threshold = 0.3, int depth = 1, CancellationToken ct = default);
}
