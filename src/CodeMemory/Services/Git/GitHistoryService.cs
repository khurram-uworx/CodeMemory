using CodeMemory.Diagnostics;
using CodeMemory.Indexing.Git;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodeMemory.Services.Git;

public sealed class GitHistoryService : IGitHistoryService, IDisposable
{
    sealed record CacheEntry(object Result, DateTime Timestamp);

    readonly IStorageService storage;
    readonly IGitMetricStore? metricStore;
    readonly ILogger<GitHistoryService> logger;
    readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly TimeSpan cacheTtl;
    readonly Timer cleanupTimer;

    public GitHistoryService(ILogger<GitHistoryService> logger, IStorageService storage,
        IGitMetricStore? metricStore = null)
    {
        this.logger = logger;
        this.storage = storage;
        this.metricStore = metricStore;
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

    async
        Task<(IReadOnlyList<HotspotInfo> Hotspots, Dictionary<string, string> FileHashes)?>
        runGitHotspotsAsync(int top, int maxCommits, CancellationToken ct)
    {
        using var activity = CodeMemoryActivitySources.Git.StartActivity("GetHotspots");
        activity?.SetTag("top", top);
        activity?.SetTag("maxCommits", maxCommits);

        var logArgs = $"--no-pager log --format=\"%H|%an|%ad\" --date=short --diff-filter=AM --max-count={maxCommits * 10} --name-only";
        var (exitCode, stdout) = await runGitAsync(logArgs, ct);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return null;

        var fileCounts = new Dictionary<string, (int commits, HashSet<string> authors, string lastDate, string lastHash)>(StringComparer.OrdinalIgnoreCase);

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
                    fileCounts[filePath] = (0, [], "", "");

                var entry = fileCounts[filePath];
                var authors = entry.authors;
                authors.Add(currentAuthor ?? "unknown");
                fileCounts[filePath] = (
                    entry.commits + 1, authors,
                    entry.commits == 0 ? currentDate! : entry.lastDate,
                    entry.commits == 0 ? currentHash! : entry.lastHash);
            }
        }

        var fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hotspots = fileCounts
            .Select(kv =>
            {
                fileHashes[kv.Key] = kv.Value.lastHash;
                return new HotspotInfo(
                    kv.Key, kv.Value.commits, kv.Value.authors.Count, kv.Value.lastDate);
            })
            .OrderByDescending(h => h.CommitCount)
            .Take(top)
            .ToList();

        logger.LogDebug("GetHotspotsAsync: {Count} hotspots from {Total} files", hotspots.Count, fileCounts.Count);
        return (hotspots, fileHashes);
    }

    async Task<string?> getLastCommitHashAsync(string filePath, CancellationToken ct)
    {
        var (exitCode, stdout) = await runGitAsync(
            $"--no-pager log -1 --format=\"%H\" -- \"{filePath}\"", ct);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout)
            ? stdout.Trim()
            : null;
    }

    async Task<(int ExitCode, string Stdout)> runGitAsync(string arguments, CancellationToken ct)
    {
        using var activity = CodeMemoryActivitySources.Git.StartActivity("GitCommand");
        activity?.SetTag("git.args", arguments);

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = storage.RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process = Process.Start(psi);
            if (process == null)
                return (1, "");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var timeoutCt = cts.Token;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCt);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCt);
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeoutCt));

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                logger.LogDebug("Git command failed ({Code}): {Args}\n{Error}",
                    process.ExitCode, arguments, stderr);
            }

            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Git command timed out after 30s: {Args}", arguments);
            if (process is { HasExited: false })
            {
                try { process.Kill(); } catch { /* best effort */ }
                await process.WaitForExitAsync(CancellationToken.None);
            }
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
        var symbol = await storage.GetSymbolByFullNameAsync(symbolPath, ct);
        if (symbol == null || string.IsNullOrEmpty(symbol.FilePath))
        {
            logger.LogDebug("GetSymbolHistoryAsync({Symbol}): symbol not found", symbolPath);
            return null;
        }

        var filePath = symbol.FilePath;
        var cacheKey = $"history:{storage.RepoRoot}:{filePath}";

        // Check in-memory cache (L1)
        if (cache.TryGetValue(cacheKey, out var cached) && cached.Result is SymbolHistoryResult cachedResult)
            return cachedResult;

        // Check persistent cache (L2) with validation
        if (metricStore != null)
        {
            var cachedEntry = await metricStore.GetAsync(filePath, ct);
            if (cachedEntry != null)
            {
                var currentHash = await getLastCommitHashAsync(filePath, ct);
                if (currentHash != null &&
                    string.Equals(currentHash, cachedEntry.LastCommitHash, StringComparison.OrdinalIgnoreCase))
                {
                    var result = new SymbolHistoryResult(
                        SymbolPath: symbolPath,
                        FilePath: filePath,
                        TotalCommits: cachedEntry.CommitCount,
                        UniqueAuthors: cachedEntry.UniqueAuthors,
                        FirstCommitDate: cachedEntry.FirstCommitDate,
                        LastCommitDate: cachedEntry.LastModified,
                        RecentCommits: cachedEntry.RecentCommits?.Take(
                            Math.Min(maxCommits, cachedEntry.RecentCommits.Count)).ToList());

                    cache[cacheKey] = new CacheEntry(result, DateTime.UtcNow);
                    return result;
                }
            }
        }

        var gitResult = await runGitHistoryAsync(filePath, maxCommits, ct);
        if (gitResult != null)
        {
            cache[cacheKey] = new CacheEntry(gitResult, DateTime.UtcNow);

            // Seed persistent cache
            if (metricStore != null)
            {
                var lastHash = await getLastCommitHashAsync(filePath, ct) ?? "";
                var entry = new GitMetricEntry(
                    filePath,
                    gitResult.TotalCommits,
                    gitResult.UniqueAuthors,
                    gitResult.LastCommitDate,
                    gitResult.FirstCommitDate,
                    lastHash,
                    gitResult.RecentCommits);
                await metricStore.SetAsync(filePath, entry, ct);
            }
        }

        return gitResult;
    }

    public async Task<IReadOnlyList<HotspotInfo>> GetHotspotsAsync(
        int top = 10, int maxCommits = 100, CancellationToken ct = default)
    {
        var cacheKey = $"hotspots:{storage.RepoRoot}:{top}:{maxCommits}";
        if (cache.TryGetValue(cacheKey, out var cached) && cached.Result is IReadOnlyList<HotspotInfo> cachedResult)
            return cachedResult;

        var result = await runGitHotspotsAsync(top, maxCommits, ct);
        if (result != null)
        {
            var (hotspots, fileHashes) = result.Value;
            cache[cacheKey] = new CacheEntry(hotspots, DateTime.UtcNow);

            // Seed persistent cache with aggregate data
            if (metricStore != null)
            {
                var entries = hotspots.ToDictionary(
                    h => h.FilePath,
                    h => new GitMetricEntry(
                        h.FilePath, h.CommitCount, h.UniqueAuthorCount,
                        h.LastModified, h.LastModified,
                        fileHashes.GetValueOrDefault(h.FilePath, "")));
                await metricStore.UpsertBatchAsync(entries, ct);
            }

            return hotspots;
        }

        return [];
    }

    public void Dispose()
    {
        cleanupTimer.Dispose();
    }
}
