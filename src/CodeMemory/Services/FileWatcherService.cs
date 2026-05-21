using CodeMemory.Indexing;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Services;

public sealed class FileWatcherService : IDisposable
{
    static string? toRelativePath(string repoRoot, string fullPath)
    {
        if (!fullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = fullPath.Substring(repoRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');
        return relative.Replace('\\', '/');
    }

    static bool isWatchedExtension(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        return LanguageDetector.SupportedExtensions.Contains(ext);
    }

    readonly string repoRoot;
    readonly IStorageService storage;
    readonly IndexingEngine engine;
    readonly ILogger<FileWatcherService> logger;
    readonly object gate = new();
    readonly HashSet<string> pendingCreations = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pendingDeletions = new(StringComparer.OrdinalIgnoreCase);

    FileSystemWatcher? watcher;
    Timer? debounceTimer;
    GitIgnoreParser gitIgnore;
    Task? currentBatch;
    bool disposed;

    const int DebounceMs = 1000;

    public FileWatcherService(
        string repoRoot,
        IStorageService storage,
        IndexingEngine engine,
        ILogger<FileWatcherService> logger)
    {
        this.repoRoot = repoRoot;
        this.storage = storage;
        this.engine = engine;
        this.logger = logger;
        gitIgnore = GitIgnoreParser.Empty;
    }

    void OnChanged(object? sender, FileSystemEventArgs e)
    {
        if (disposed) return;
        if (Directory.Exists(e.FullPath)) return;
        if (!isWatchedExtension(e.FullPath)) return;
        if (isGitIgnored(e.FullPath)) return;
        enqueueForReindex(e.FullPath);
    }

    void OnCreated(object? sender, FileSystemEventArgs e)
    {
        if (disposed) return;
        if (Directory.Exists(e.FullPath)) return;
        if (!isWatchedExtension(e.FullPath)) return;
        if (isGitIgnored(e.FullPath)) return;
        enqueueForReindex(e.FullPath);
    }

    void OnDeleted(object? sender, FileSystemEventArgs e)
    {
        if (disposed) return;
        if (!isWatchedExtension(e.FullPath)) return;
        if (isGitIgnored(e.FullPath)) return;

        lock (gate)
        {
            pendingDeletions.Add(e.FullPath);
            pendingCreations.Remove(e.FullPath);
        }
        resetDebounceTimer();
    }

    void OnRenamed(object? sender, RenamedEventArgs e)
    {
        if (disposed) return;

        if (isWatchedExtension(e.OldFullPath) && !isGitIgnored(e.OldFullPath))
        {
            lock (gate)
            {
                pendingDeletions.Add(e.OldFullPath);
                pendingCreations.Remove(e.OldFullPath);
            }
        }

        if (isWatchedExtension(e.FullPath) && !isGitIgnored(e.FullPath))
        {
            enqueueForReindex(e.FullPath);
        }

        resetDebounceTimer();
    }

    void OnError(object? sender, ErrorEventArgs e)
    {
        logger.LogWarning(e.GetException(), "FileSystemWatcher buffer overflow or error");
    }

    bool isGitIgnored(string fullPath)
    {
        var relative = toRelativePath(fullPath);
        return relative != null && gitIgnore.IsIgnored(relative);
    }

    string? toRelativePath(string fullPath)
        => toRelativePath(repoRoot, fullPath);

    void enqueueForReindex(string fullPath)
    {
        lock (gate)
        {
            pendingCreations.Add(fullPath);
            pendingDeletions.Remove(fullPath);
        }
        resetDebounceTimer();
    }

    void resetDebounceTimer()
    {
        lock (gate)
        {
            if (debounceTimer == null)
                debounceTimer = new Timer(_ => onDebounceElapsed(), null, DebounceMs, Timeout.Infinite);
            else
                debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    void onDebounceElapsed()
    {
        if (disposed) return;

        string[] creates;
        string[] deletes;

        lock (gate)
        {
            creates = [.. pendingCreations];
            deletes = [.. pendingDeletions];
            pendingCreations.Clear();
            pendingDeletions.Clear();
        }

        if (creates.Length == 0 && deletes.Length == 0)
            return;

        logger.LogInformation(
            "Debounce elapsed — {Creates} files to index, {Deletes} files to delete",
            creates.Length, deletes.Length);

        Task batch;
        lock (gate)
        {
            batch = processBatchAsync(creates, deletes);
            currentBatch = batch;
        }
    }

    async Task processBatchAsync(string[] creates, string[] deletes)
    {
        foreach (var fullPath in deletes)
        {
            if (disposed) return;

            try
            {
                var relativePath = toRelativePath(fullPath);
                if (relativePath == null) continue;

                var symbols = await storage.GetSymbolsByFileAsync(relativePath, top: 10000);
                var symbolIds = symbols.Select(s => s.Id).ToList();

                if (symbolIds.Count > 0)
                {
                    await storage.DeleteRelationshipsBySourceIdsAsync(symbolIds);
                    await storage.DeleteRelationshipsByTargetIdsAsync(symbolIds);
                }

                await storage.DeleteSymbolsByFileAsync(relativePath);
                await storage.DeleteChunksByFileAsync(relativePath);

                logger.LogDebug("Deleted index data for {Path}", relativePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete index data for {Path}", fullPath);
            }
        }

        foreach (var fullPath in creates)
        {
            if (disposed) return;

            try
            {
                var relativePath = toRelativePath(fullPath);
                if (relativePath == null) continue;

                var symbols = await storage.GetSymbolsByFileAsync(relativePath, top: 10000);
                var symbolIds = symbols.Select(s => s.Id).ToList();

                if (symbolIds.Count > 0)
                {
                    await storage.DeleteRelationshipsBySourceIdsAsync(symbolIds);
                    await storage.DeleteRelationshipsByTargetIdsAsync(symbolIds);
                }

                await storage.DeleteSymbolsByFileAsync(relativePath);
                await storage.DeleteChunksByFileAsync(relativePath);

                var result = await engine.ProcessFileAsync(fullPath, CancellationToken.None);

                if (result.Symbols.Count > 0)
                {
                    await storage.StoreSymbolsAsync(result.Symbols);
                    await storage.StoreRelationshipsAsync(result.Relationships);
                }

                if (result.Chunks.Count > 0)
                    await storage.StoreChunksAsync(result.Chunks);

                logger.LogDebug(
                    "Re-indexed {Path} — {Symbols} symbols, {Chunks} chunks, {Relationships} relationships",
                    relativePath, result.Symbols.Count, result.Chunks.Count, result.Relationships.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to re-index {Path}", fullPath);
            }
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (watcher != null)
            return Task.CompletedTask;

        gitIgnore = GitIgnoreParser.Load(Path.Combine(repoRoot, ".gitignore"));

        watcher = new FileSystemWatcher(repoRoot)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = true,
            InternalBufferSize = 65536,
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnCreated;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        watcher.EnableRaisingEvents = true;

        logger.LogInformation("File watcher started for {RepoRoot}", repoRoot);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnCreated;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
            watcher = null;
        }

        lock (gate)
        {
            debounceTimer?.Dispose();
            debounceTimer = null;
            pendingCreations.Clear();
            pendingDeletions.Clear();
        }

        Task? batch;
        lock (gate)
            batch = currentBatch;

        if (batch != null)
        {
            try { await batch; }
            catch { /* batch was stopped */ }
        }

        logger.LogInformation("File watcher stopped");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}
