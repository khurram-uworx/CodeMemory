using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Storage;
using CodeMemory.Diagnostics;
using CodeMemory.Indexing;
using CodeMemory.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace CodeMemory.AspNet.Services;

public sealed class IndexingHostedService : BackgroundService
{
    readonly IServiceProvider serviceProvider;
    readonly IRepoContextAccessor repoContext;
    readonly ILogger<IndexingHostedService> logger;
    readonly IndexingOptions indexingOptions;
    readonly StorageFactory storageFactory;
    readonly CloneIndexService cloneIndex;

    public IndexingHostedService(IServiceProvider serviceProvider,
        IRepoContextAccessor repoContext, ILogger<IndexingHostedService> logger,
        IOptions<IndexingOptions> indexingOptions,
        StorageFactory storageFactory,
        CloneIndexService cloneIndex)
    {
        this.serviceProvider = serviceProvider;
        this.repoContext = repoContext;
        this.logger = logger;
        this.indexingOptions = indexingOptions.Value;
        this.storageFactory = storageFactory;
        this.cloneIndex = cloneIndex;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Indexing hosted service starting");

        // Small delay to let startup bootstrap finish
        await Task.Delay(500, stoppingToken);

        var dbFactory = serviceProvider.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();
        var registry = serviceProvider.GetRequiredService<IServiceRegistry>();

        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

        var pendingRepos = await db.RegisteredRepos
            .Where(r => r.CloneStatus == "Pending" || r.IndexStatus == "Pending")
            .ToListAsync(stoppingToken);

        pendingRepos = pendingRepos.Where(r => !cloneIndex.IsProcessing(r.Name)).ToList();

        if (pendingRepos.Count == 0)
        {
            logger.LogInformation("No pending repos to index");
            return;
        }

        foreach (var repo in pendingRepos)
        {
            if (stoppingToken.IsCancellationRequested) break;

            using var metricsScope = CodeMemoryMetrics.BeginRepoScope(repo.Name);
            var repoTimeout = TimeSpan.FromMinutes(indexingOptions.RepoTimeoutMinutes);
            using var repoCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            repoCts.CancelAfter(repoTimeout);
            var repoCt = repoCts.Token;

            try
            {
                if (repo.CloneStatus == "Pending" && !string.IsNullOrEmpty(repo.GitUrl))
                {
                    using var cloneActivity = CodeMemoryActivitySources.Git.StartActivity("Clone");
                    cloneActivity?.SetTag("repo.name", repo.Name);
                    cloneActivity?.SetTag("repo.url", repo.GitUrl);
                    var cloneSw = Stopwatch.StartNew();

                    logger.LogInformation("Cloning repository '{Name}' from {Url}", repo.Name, repo.GitUrl);

                    await UpdateCloneStatusAsync(dbFactory, repo.Name, "Cloning", ct: repoCt);

                    // Stale directory from prior deletion may exist; remove it so clone succeeds
                    DirectoryHelper.ForceDelete(repo.LocalPath);

                    var psi = new ProcessStartInfo("git")
                    {
                        Arguments = string.IsNullOrEmpty(repo.Branch)
                            ? $"clone --depth 1 \"{repo.GitUrl}\" \"{repo.LocalPath}\""
                            : $"clone --branch \"{repo.Branch}\" --depth 1 \"{repo.GitUrl}\" \"{repo.LocalPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };

                    using var process = Process.Start(psi)
                        ?? throw new InvalidOperationException("Failed to start git process.");

                    await process.WaitForExitAsync(repoCt);
                    if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync(repoCt);
                        throw new InvalidOperationException($"git clone failed: {error}");
                    }

                    cloneSw.Stop();
                    CodeMemoryMetrics.CloneDuration.Record(cloneSw.Elapsed.TotalMilliseconds,
                        new("repo.name", repo.Name), new("repo.url", repo.GitUrl));
                }

                if (repo.CloneStatus != "Cloned")
                {
                    await UpdateCloneStatusAsync(dbFactory, repo.Name, "Cloned", localPath: repo.LocalPath, ct: repoCt);
                }

                // Initialize storage if not already registered
                try
                {
                    registry.GetStorage(repo.Name);
                }
                catch
                {
                    var storage = storageFactory(repo.Name, repo.LocalPath, repo.Id);
                    registry.Register(repo.Name, storage);
                }

                await UpdateIndexStatusAsync(dbFactory, repo.Name, "Indexing", ct: repoCt);

                repoContext.CurrentRepoName = repo.Name;
                repoContext.CurrentRepoRoot = repo.LocalPath;

                var indexResult = await indexWithRetryAsync(repo.Name, repo.LocalPath, repoCt);

                IndexingState.MarkCompleted(repo.Name);
                IndexingState.StoreRelationshipCount(repo.Name, indexResult.RelationshipCount);
                await UpdateIndexStatusAsync(dbFactory, repo.Name, "Indexed", ct: repoCt);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Indexing cancelled for repository '{Name}'", repo.Name);
                IndexingState.ClearProgress(repo.Name);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing repository '{Name}'", repo.Name);
                IndexingState.ClearProgress(repo.Name);
                var statusField = repo.CloneStatus == "Pending" ? "CloneStatus" : "IndexStatus";
                if (statusField == "CloneStatus")
                    await UpdateCloneStatusAsync(dbFactory, repo.Name, "Failed", errorMessage: ex.Message, ct: stoppingToken);
                else
                    await UpdateIndexStatusAsync(dbFactory, repo.Name, "Failed", errorMessage: ex.Message, ct: stoppingToken);
            }
            finally
            {
                repoContext.CurrentRepoName = null;
                repoContext.CurrentRepoRoot = null;
            }
        }

        logger.LogInformation("Indexing hosted service completed");
    }

    async Task<IndexingResult> indexWithRetryAsync(string repoName, string repoPath, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, indexingOptions.RetryCount + 1);
        var baseDelay = TimeSpan.FromSeconds(indexingOptions.RetryBaseDelaySeconds);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var scope = serviceProvider.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();

                var progress = new Progress<double>(p =>
                {
                    IndexingState.UpdateProgress(repoName, p);
                });

                var result = await engine.RunIndexingAsync(repoPath, ct, progress);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = baseDelay * (int)Math.Pow(2, attempt - 1);
                logger.LogWarning(ex,
                    "Indexing attempt {Attempt}/{Max} failed for '{Repo}', retrying in {Delay}s",
                    attempt, maxAttempts, repoName, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException($"All {maxAttempts} indexing attempts failed for '{repoName}'");
    }

    static async Task UpdateCloneStatusAsync(IDbContextFactory<RepoRegistryDbContext> dbFactory,
        string name, string status, string? localPath = null, string? errorMessage = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name, ct);
        if (repo is null) return;

        repo.CloneStatus = status;
        if (localPath is not null) repo.LocalPath = localPath;
        if (errorMessage is not null) repo.ErrorMessage = errorMessage;
        await db.SaveChangesAsync(ct);
    }

    static async Task UpdateIndexStatusAsync(IDbContextFactory<RepoRegistryDbContext> dbFactory,
        string name, string status, string? errorMessage = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name, ct);
        if (repo is null) return;

        repo.IndexStatus = status;
        if (status == "Indexed") repo.LastIndexedAt = DateTime.UtcNow;
        if (errorMessage is not null) repo.ErrorMessage = errorMessage;
        await db.SaveChangesAsync(ct);
    }
}
