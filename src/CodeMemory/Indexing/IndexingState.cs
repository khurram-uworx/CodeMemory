using System.Collections.Concurrent;

namespace CodeMemory.Indexing;

public static class IndexingState
{
    static readonly ConcurrentDictionary<string, bool> repoCompleted = new(StringComparer.OrdinalIgnoreCase);
    static volatile bool fileWatcherActive;

    public static bool IsCompleted(string? repoName = null)
        => repoName is null
            ? repoCompleted.Values.All(v => v) && repoCompleted.Count > 0
            : repoCompleted.GetValueOrDefault(repoName, false);

public static void MarkCompleted(string repoName)
    => repoCompleted[repoName] = true;

public static void MarkIncomplete(string repoName)
    => repoCompleted.TryRemove(repoName, out _);

    public static bool IsFileWatcherActive => fileWatcherActive;

    public static void MarkFileWatcherActive()
        => fileWatcherActive = true;
}
