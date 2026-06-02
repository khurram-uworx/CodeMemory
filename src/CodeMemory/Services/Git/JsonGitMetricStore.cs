using CodeMemory.Indexing.Git;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeMemory.Services.Git;

public sealed class JsonGitMetricStore : IGitMetricStore, IDisposable
{
    sealed record StoreData(
        int Version,
        string UpdatedAt,
        Dictionary<string, EntryData> Entries);

    sealed record EntryData(
        int CommitCount,
        int UniqueAuthors,
        string LastModified,
        string LastCommitHash,
        string FirstCommitDate,
        List<CommitData>? RecentCommits);

    sealed record CommitData(string Hash, string Author, string Date, string Message);

    static Dictionary<string, EntryData> createEntryDictionary()
        => new(StringComparer.OrdinalIgnoreCase);

    static GitMetricEntry toMetricEntry(string filePath, EntryData entry)
    {
        return new GitMetricEntry(
            filePath,
            entry.CommitCount,
            entry.UniqueAuthors,
            entry.LastModified,
            entry.FirstCommitDate,
            entry.LastCommitHash,
            entry.RecentCommits?.Select(c => new CommitInfo(c.Hash, c.Author, c.Date, c.Message)).ToList());
    }

    static EntryData fromMetricEntry(GitMetricEntry entry)
    {
        return new EntryData(
            entry.CommitCount,
            entry.UniqueAuthors,
            entry.LastModified,
            entry.LastCommitHash,
            entry.FirstCommitDate,
            entry.RecentCommits?.Select(c => new CommitData(c.Hash, c.Author, c.Date, c.Message)).ToList());
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    readonly IStorageService storage;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly ILogger<JsonGitMetricStore> logger;
    bool disposed;

    public JsonGitMetricStore(IStorageService storage, ILogger<JsonGitMetricStore> logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    string repoRoot => storage.RepoRoot;

    string storePath => Path.Combine(storage.RepoRoot, ".codememory", "git-metrics.json");

    string? normalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');

        // Collapse double slashes
        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");

        // Strip leading ./
        if (normalized.StartsWith("./"))
            normalized = normalized[2..];

        // Relativize absolute paths
        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':'))
        {
            var repoPrefix = repoRoot.Replace('\\', '/');
            if (!repoPrefix.EndsWith('/'))
                repoPrefix += '/';

            if (normalized.StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[repoPrefix.Length..];
            else
            {
                logger.LogWarning("Path {Path} is outside repo root {RepoRoot} — rejecting", path, repoRoot);
                return null;
            }
        }

        return normalized;
    }

    StoreData? readStore()
    {
        try
        {
            var path = storePath;
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<StoreData>(json, JsonOptions);
            if (data != null && data.Entries.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                var entries = createEntryDictionary();
                foreach (var kv in data.Entries)
                    entries[kv.Key] = kv.Value;
                data = data with { Entries = entries };
            }
            return data;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read git metric cache at {Path}", storePath);
            return null;
        }
    }

    void writeStore(StoreData data)
    {
        try
        {
            var path = storePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var tmpPath = path + ".tmp";
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write git metric cache at {Path}", storePath);
        }
    }

    public Task<GitMetricEntry?> GetAsync(string filePath, CancellationToken ct = default)
    {
        var normalized = normalizePath(filePath);
        if (normalized == null)
            return Task.FromResult<GitMetricEntry?>(null);
        var data = readStore();
        if (data?.Entries.TryGetValue(normalized, out var entry) == true)
            return Task.FromResult<GitMetricEntry?>(toMetricEntry(normalized, entry));
        return Task.FromResult<GitMetricEntry?>(null);
    }

    public Task<IReadOnlyDictionary<string, GitMetricEntry>> GetAllAsync(CancellationToken ct = default)
    {
        var data = readStore();
        if (data == null)
            return Task.FromResult<IReadOnlyDictionary<string, GitMetricEntry>>(
                new Dictionary<string, GitMetricEntry>(StringComparer.OrdinalIgnoreCase));

        var result = new Dictionary<string, GitMetricEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in data.Entries)
            result[kv.Key] = toMetricEntry(kv.Key, kv.Value);
        return Task.FromResult<IReadOnlyDictionary<string, GitMetricEntry>>(result);
    }

    public async Task SetAsync(string filePath, GitMetricEntry entry, CancellationToken ct = default)
    {
        var normalized = normalizePath(filePath);
        if (normalized == null)
        {
            logger.LogWarning("Skipping SetAsync for invalid path: {Path}", filePath);
            return;
        }
        await writeLock.WaitAsync(ct);
        try
        {
            var data = readStore() ?? new StoreData(1, "", createEntryDictionary());
            data.Entries[normalized] = fromMetricEntry(entry);
            data = data with { UpdatedAt = DateTime.UtcNow.ToString("O") };
            writeStore(data);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task ReplaceAllAsync(IReadOnlyDictionary<string, GitMetricEntry> entries, CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            var newEntries = new Dictionary<string, EntryData>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in entries)
            {
                var normalized = normalizePath(kv.Key);
                if (normalized != null)
                    newEntries[normalized] = fromMetricEntry(kv.Value);
                else
                    logger.LogWarning("Skipping ReplaceAll entry with invalid path: {Path}", kv.Key);
            }
            var data = new StoreData(1, DateTime.UtcNow.ToString("O"), newEntries);
            writeStore(data);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task UpsertBatchAsync(IReadOnlyDictionary<string, GitMetricEntry> entries, CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            var data = readStore() ?? new StoreData(1, "", createEntryDictionary());
            foreach (var kv in entries)
            {
                var normalized = normalizePath(kv.Key);
                if (normalized != null)
                    data.Entries[normalized] = fromMetricEntry(kv.Value);
                else
                    logger.LogWarning("Skipping UpsertBatch entry with invalid path: {Path}", kv.Key);
            }
            data = data with { UpdatedAt = DateTime.UtcNow.ToString("O") };
            writeStore(data);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            writeLock.Dispose();
            disposed = true;
        }
    }
}
