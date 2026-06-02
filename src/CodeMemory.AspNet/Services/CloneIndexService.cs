using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Storage;
using CodeMemory.Diagnostics;
using CodeMemory.Indexing;
using CodeMemory.Services;

using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodeMemory.AspNet.Services;

static class DirectoryHelper
{
    public static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}

public sealed class CloneIndexService
{
    readonly IDbContextFactory<RepoRegistryDbContext> contextFactory;
    readonly IServiceRegistry storageRegistry;
    readonly IRepoContextAccessor repoContext;
    readonly IServiceScopeFactory scopeFactory;
    readonly ILogger<CloneIndexService> logger;
    readonly ILoggerFactory loggerFactory;
    readonly RepoRegistryOptions registryOptions;
    readonly StorageFactory storageFactory;
    readonly RepoMetricsRecorder metricsRecorder;
    readonly ConcurrentDictionary<string, bool> inProgress = new(StringComparer.OrdinalIgnoreCase);

    public CloneIndexService(
        IDbContextFactory<RepoRegistryDbContext> contextFactory,
        IServiceRegistry storageRegistry,
        IRepoContextAccessor repoContext,
        IServiceScopeFactory scopeFactory,
        ILogger<CloneIndexService> logger,
        ILoggerFactory loggerFactory,
        RepoRegistryOptions registryOptions,
        StorageFactory storageFactory,
        RepoMetricsRecorder metricsRecorder)
    {
        this.contextFactory = contextFactory;
        this.storageRegistry = storageRegistry;
        this.repoContext = repoContext;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
        this.registryOptions = registryOptions;
        this.storageFactory = storageFactory;
        this.metricsRecorder = metricsRecorder;
    }

    public static bool IsGitUrl(string source)
    {
        if (source.Contains("://"))
            return true;

        // SSH SCP-style: [user@]host:path (e.g., git@github.com:user/repo.git)
        if (source.Contains('@') && source.Contains(':'))
        {
            var atIndex = source.IndexOf('@');
            var colonIndex = source.IndexOf(':');
            if (atIndex > 0 && colonIndex > atIndex)
            {
                var hostPart = source[(atIndex + 1)..colonIndex];
                if (!hostPart.Contains('/') && !hostPart.Contains('\\'))
                    return true;
            }
        }

        return false;
    }

    public static string ResolveRepoPath(string source, string repoName, RepoRegistryOptions options)
    {
        if (IsGitUrl(source))
            return Path.GetFullPath(Path.Combine(Path.GetFullPath(options.CloneBasePath), repoName));
        return Path.GetFullPath(source);
    }

    public bool IsProcessing(string repoName)
        => inProgress.ContainsKey(repoName);

    public Task EnqueueRepoAsync(string repoName, string source, string? branch)
    {
        if (!inProgress.TryAdd(repoName, true))
        {
            logger.LogWarning("Repo '{RepoName}' is already being cloned/indexed — skipping duplicate", repoName);
            return Task.CompletedTask;
        }

        var isUrl = IsGitUrl(source);
        var clonePath = isUrl ? Path.GetFullPath(Path.Combine(Path.GetFullPath(registryOptions.CloneBasePath), repoName)) : source;

        _ = Task.Run(async () =>
        {
            try
            {
                if (isUrl)
                {
                    using var cloneActivity = CodeMemoryActivitySources.Git.StartActivity("Clone");
                    cloneActivity?.SetTag(CodeMemoryMetrics.Tags.Repo, repoName);
                    cloneActivity?.SetTag(CodeMemoryMetrics.Tags.RepoUrl, source);
                    var cloneSw = Stopwatch.StartNew();

                    await UpdateCloneStatusAsync(repoName, "Cloning");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(registryOptions.GitCommandTimeoutSeconds));

                    // Stale directory from prior deletion may exist; remove it so clone succeeds
                    DirectoryHelper.ForceDelete(clonePath);

                    var psi = new ProcessStartInfo("git")
                    {
                        Arguments = string.IsNullOrEmpty(branch)
                            ? $"clone --depth 1 \"{source}\" \"{clonePath}\""
                            : $"clone --branch \"{branch}\" --depth 1 \"{source}\" \"{clonePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };

                    using var process = Process.Start(psi)
                        ?? throw new InvalidOperationException("Failed to start git process.");

                    await process.WaitForExitAsync(cts.Token);
                    if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        throw new InvalidOperationException($"git clone failed: {error}");
                    }

                    cloneSw.Stop();
                    CodeMemoryMetrics.CloneDuration.Record(cloneSw.Elapsed.TotalMilliseconds,
                        new(CodeMemoryMetrics.Tags.Repo, repoName), new(CodeMemoryMetrics.Tags.RepoUrl, source));

                    await UpdateCloneStatusAsync(repoName, "Cloned", localPath: clonePath);
                }
                else
                    await UpdateCloneStatusAsync(repoName, "Cloned", localPath: clonePath);

                await InitializeAndIndexAsync(repoName, clonePath);
            }
            catch (OperationCanceledException)
            {
                var msg = $"git clone timed out after {registryOptions.GitCommandTimeoutSeconds} seconds";
                logger.LogError("Repo '{RepoName}': {Msg}", repoName, msg);
                await UpdateCloneStatusAsync(repoName, "Failed", errorMessage: msg);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process repo '{RepoName}'", repoName);
                await UpdateCloneStatusAsync(repoName, "Failed", errorMessage: ex.Message);
            }
            finally
            {
                inProgress.TryRemove(repoName, out _);
            }
        });

        return Task.CompletedTask;
    }

    async Task InitializeAndIndexAsync(string repoName, string repoPath)
    {
        using var activity = CodeMemoryActivitySources.Indexing.StartActivity("InitializeAndIndex");
        activity?.SetTag(CodeMemoryMetrics.Tags.Repo, repoName);
        activity?.SetTag("repo.path", repoPath);

        var repoEntity = await GetRepoAsync(repoName);
        var repoId = repoEntity?.Id ?? 0;
        var storage = storageFactory(repoName, repoPath, repoId);

        storageRegistry.Register(repoName, storage);

        await UpdateIndexStatusAsync(repoName, "Indexing");

        try
        {
            repoContext.CurrentRepoName = repoName;
            repoContext.CurrentRepoRoot = repoPath;

            using var scope = scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
            var result = await engine.RunIndexingAsync(repoPath, CancellationToken.None);

            IndexingState.MarkCompleted(repoName);
            IndexingState.StoreRelationshipCount(repoName, result.RelationshipCount);
            await metricsRecorder.RecordAsync(repoName);
            await UpdateIndexStatusAsync(repoName, "Indexed");
        }
        finally
        {
            repoContext.CurrentRepoName = null;
            repoContext.CurrentRepoRoot = null;
        }
    }

    public async Task DeleteRepoAsync(string repoName)
    {
        var repo = await GetRepoAsync(repoName);
        if (repo is null) return;

        storageRegistry.Unregister(repoName);
        metricsRecorder.RemoveRepo(repoName);

        if (!string.IsNullOrEmpty(repo.GitUrl))
        {
            try { DirectoryHelper.ForceDelete(repo.LocalPath); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete cloned directory for '{Repo}'", repoName); }
        }

        await using var db = await contextFactory.CreateDbContextAsync();
        var entity = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == repoName);
        if (entity is not null)
        {
            db.RegisteredRepos.Remove(entity);
            await db.SaveChangesAsync();
        }
    }

    async Task UpdateCloneStatusAsync(string name, string status, string? localPath = null, string? errorMessage = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var repo = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
        if (repo is null) return;

        repo.CloneStatus = status;
        if (localPath is not null) repo.LocalPath = localPath;
        if (errorMessage is not null) repo.ErrorMessage = errorMessage;

        await db.SaveChangesAsync();
    }

    async Task UpdateIndexStatusAsync(string name, string status, string? errorMessage = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var repo = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
        if (repo is null) return;

        repo.IndexStatus = status;
        if (status == "Indexed") repo.LastIndexedAt = DateTime.UtcNow;
        if (errorMessage is not null) repo.ErrorMessage = errorMessage;

        await db.SaveChangesAsync();
    }

    async Task<Repositories?> GetRepoAsync(string name)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
    }
}
