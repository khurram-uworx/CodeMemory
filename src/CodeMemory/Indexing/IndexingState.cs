using System.Collections.Concurrent;

namespace CodeMemory.Indexing;

public static class IndexingState
{
    static readonly ConcurrentDictionary<string, bool> repoCompleted = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, double> repoProgress = new(StringComparer.OrdinalIgnoreCase);
    static volatile bool fileWatcherActive;

    public static bool IsCompleted(string? repoName = null)
        => repoName is null
            ? repoCompleted.Values.All(v => v) && repoCompleted.Count > 0
            : repoCompleted.GetValueOrDefault(repoName, false);

    public static void MarkCompleted(string repoName)
        => repoCompleted[repoName] = true;

    public static void MarkIncomplete(string repoName)
        => repoCompleted.TryRemove(repoName, out _);

    public static double? GetProgress(string? repoName)
        => repoName is not null && repoProgress.TryGetValue(repoName, out var p) ? p : null;

    public static IReadOnlyDictionary<string, double> GetAllProgress()
        => repoProgress;

    public static void UpdateProgress(string repoName, double pct)
        => repoProgress[repoName] = pct;

    public static void ClearProgress(string repoName)
        => repoProgress.TryRemove(repoName, out _);

    public static bool IsFileWatcherActive => fileWatcherActive;

    public static void MarkFileWatcherActive()
        => fileWatcherActive = true;
}
