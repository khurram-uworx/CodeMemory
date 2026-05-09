namespace CodeMemory.Indexing.Architecture;

public sealed record ComponentCluster(
    string Name,
    IReadOnlyList<string> Members,
    double CohesionScore);

public interface IComponentClusteringService
{
    Task<IReadOnlyList<ComponentCluster>> GetClustersAsync(
        double threshold = 0.3, CancellationToken ct = default);
}
