namespace CodeMemory.Indexing.Git;

public sealed record CommitInfo(
    string Hash,
    string Author,
    string Date,
    string Message);

public sealed record SymbolHistoryResult(
    string SymbolPath,
    string FilePath,
    int TotalCommits,
    int UniqueAuthors,
    string FirstCommitDate,
    string LastCommitDate,
    IReadOnlyList<CommitInfo>? RecentCommits = null);

public sealed record HotspotInfo(
    string FilePath,
    int CommitCount,
    int UniqueAuthorCount,
    string LastModified);

public interface IGitHistoryService
{
    Task<SymbolHistoryResult?> GetSymbolHistoryAsync(
        string symbolPath, int maxCommits = 20, CancellationToken ct = default);

    Task<IReadOnlyList<HotspotInfo>> GetHotspotsAsync(
        int top = 10, int maxCommits = 100, CancellationToken ct = default);
}
