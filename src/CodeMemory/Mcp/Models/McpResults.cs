namespace CodeMemory.Mcp.Models;

public sealed record PingResult(
    string Status,
    bool IndexingCompleted,
    bool? FileWatcherActive = null,
    string? Repo = null,
    string? Message = null
);

public sealed record AdminRescanResult(
    string Status,
    string RepoRoot,
    string Message
);

public sealed record AdminRepositoryRootResult(
    string Status,
    string RepoRoot
);
