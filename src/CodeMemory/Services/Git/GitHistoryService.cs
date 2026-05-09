using CodeMemory.Indexing.Git;
using CodeMemory.Storage.Services;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodeMemory.Services.Git;

public sealed class GitHistoryService : IGitHistoryService, IDisposable
{
    sealed record CacheEntry(object Result, DateTime Timestamp);

    readonly IStorageService storage;
    readonly ILogger<GitHistoryService> logger;
    readonly string repoRoot;
    readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly TimeSpan cacheTtl;
    readonly Timer cleanupTimer;

    public GitHistoryService(IStorageService storage, ILogger<GitHistoryService> logger)
        : this(storage, logger, Directory.GetCurrentDirectory())
    {
    }

    public GitHistoryService(IStorageService storage, ILogger<GitHistoryService> logger, string repoRoot)
    {
        this.storage = storage;
        this.logger = logger;
        this.repoRoot = repoRoot;
        cacheTtl = TimeSpan.FromMinutes(5);
        cleanupTimer = new Timer(_ => cleanupCache(), null, cacheTtl, cacheTtl);
    }

    async Task<SymbolHistoryResult?> runGitHistoryAsync(string filePath, int maxCommits, CancellationToken ct)
    {
        var logArgs = $"--no-pager log --format=\"%H|%an|%ad|%s\" --date=short --max-count={maxCommits} -- \"{filePath}\"";
        var (exitCode, stdout) = await runGitAsync(logArgs, ct);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogDebug("Git history empty for {File}", filePath);
            return null;
        }

        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var commits = new List<CommitInfo>();

        foreach (var line in lines)
        {
            var parts = line.Split('|', 4);
            if (parts.Length >= 4)
            {
                commits.Add(new CommitInfo(
                    parts[0], parts[1], parts[2], parts[3]));
            }
        }

        if (commits.Count == 0)
            return null;

        var authors = commits.Select(c => c.Author).Distinct().Count();
        return new SymbolHistoryResult(
            SymbolPath: filePath,
            FilePath: filePath,
            TotalCommits: commits.Count,
            UniqueAuthors: authors,
            FirstCommitDate: commits[^1].Date,
            LastCommitDate: commits[0].Date,
            RecentCommits: commits.Take(10).ToList());
    }

    async Task<IReadOnlyList<HotspotInfo>?> runGitHotspotsAsync(int top, int maxCommits, CancellationToken ct)
    {
        var logArgs = $"--no-pager log --format=\"%H|%an|%ad\" --date=short --diff-filter=AM --max-count={maxCommits * 10} --name-only";
        var (exitCode, stdout) = await runGitAsync(logArgs, ct);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return [];

        var fileCounts = new Dictionary<string, (int commits, HashSet<string> authors, string lastDate)>(StringComparer.OrdinalIgnoreCase);

        var blocks = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? currentHash = null, currentAuthor = null, currentDate = null;

        foreach (var line in blocks)
        {
            var parts = line.Split('|');
            if (parts.Length == 3)
            {
                currentHash = parts[0];
                currentAuthor = parts[1];
                currentDate = parts[2];
            }
            else if (!string.IsNullOrWhiteSpace(line) && currentHash != null)
            {
                var filePath = line.Trim();
                if (!fileCounts.ContainsKey(filePath))
                    fileCounts[filePath] = (0, [], "");

                var entry = fileCounts[filePath];
                var authors = entry.authors;
                authors.Add(currentAuthor ?? "unknown");
                fileCounts[filePath] = (entry.commits + 1, authors, currentDate!);
            }
        }

        var hotspots = fileCounts
            .Select(kv => new HotspotInfo(
                kv.Key, kv.Value.commits, kv.Value.authors.Count, kv.Value.lastDate))
            .OrderByDescending(h => h.CommitCount)
            .Take(top)
            .ToList();

        logger.LogDebug("GetHotspotsAsync: {Count} hotspots from {Total} files", hotspots.Count, fileCounts.Count);
        return hotspots;
    }

    async Task<(int ExitCode, string Stdout)> runGitAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (1, "");

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                logger.LogDebug("Git command failed ({Code}): {Args}\n{Error}",
                    process.ExitCode, arguments, stderr);
            }

            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            return (1, "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Git not available or not a git repository");
            return (1, "");
        }
    }

    void cleanupCache()
    {
        var cutoff = DateTime.UtcNow - cacheTtl;
        foreach (var kv in cache)
        {
            if (kv.Value.Timestamp < cutoff)
                cache.TryRemove(kv.Key, out _);
        }
    }

    public async Task<SymbolHistoryResult?> GetSymbolHistoryAsync(
        string symbolPath, int maxCommits = 20, CancellationToken ct = default)
    {
        var symbol = await storage.GetSymbolAsync(symbolPath, ct);
        if (symbol == null || string.IsNullOrEmpty(symbol.FilePath))
        {
            logger.LogDebug("GetSymbolHistoryAsync({Symbol}): symbol not found", symbolPath);
            return null;
        }

        var cacheKey = $"history:{symbol.FilePath}";
        if (cache.TryGetValue(cacheKey, out var cached) && cached.Result is SymbolHistoryResult cachedResult)
            return cachedResult;

        var filePath = symbol.FilePath.Replace('/', Path.DirectorySeparatorChar);
        var result = await runGitHistoryAsync(filePath, maxCommits, ct);
        if (result != null)
            cache[cacheKey] = new CacheEntry(result, DateTime.UtcNow);

        return result;
    }

    public async Task<IReadOnlyList<HotspotInfo>> GetHotspotsAsync(
        int top = 10, int maxCommits = 100, CancellationToken ct = default)
    {
        const string cacheKey = "hotspots";
        if (cache.TryGetValue(cacheKey, out var cached) && cached.Result is IReadOnlyList<HotspotInfo> cachedResult)
            return cachedResult;

        var result = await runGitHotspotsAsync(top, maxCommits, ct);
        if (result != null)
            cache[cacheKey] = new CacheEntry(result, DateTime.UtcNow);

        return result ?? [];
    }

    public void Dispose()
    {
        cleanupTimer.Dispose();
    }
}
