namespace CodeMemory.Indexing.Git;

public sealed record GitMetricEntry(
    string FilePath,
    int CommitCount,
    int UniqueAuthors,
    string LastModified,
    string FirstCommitDate,
    string LastCommitHash,
    IReadOnlyList<CommitInfo>? RecentCommits = null);

public interface IGitMetricStore
{
    Task<GitMetricEntry?> GetAsync(string filePath, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, GitMetricEntry>> GetAllAsync(CancellationToken ct = default);

    Task SetAsync(string filePath, GitMetricEntry entry, CancellationToken ct = default);

    Task ReplaceAllAsync(IReadOnlyDictionary<string, GitMetricEntry> entries, CancellationToken ct = default);

    Task UpsertBatchAsync(IReadOnlyDictionary<string, GitMetricEntry> entries, CancellationToken ct = default);
}
