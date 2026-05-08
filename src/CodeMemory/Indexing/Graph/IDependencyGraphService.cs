namespace CodeMemory.Indexing.Graph;

public sealed record DependencyNode(
    string SymbolName,
    string FilePath,
    string Kind,
    string LineRange,
    string RelationType,
    IReadOnlyList<DependencyNode>? Children = null);

public enum RelationType
{
    Calls,
    Imports,
    References,
    Implements,
    Inherits,
    TestCoverage
}

public interface IDependencyGraphService
{
    Task<IReadOnlyList<DependencyNode>> TraceAsync(
        string symbolPath, string direction, int depth, CancellationToken ct = default);

    Task<IReadOnlyList<DependencyNode>> FindRelatedAsync(
        string symbolPath, string relationType, CancellationToken ct = default);

    Task<IReadOnlyList<string>> FindTestCoverageAsync(
        string symbolPath, CancellationToken ct = default);
}
