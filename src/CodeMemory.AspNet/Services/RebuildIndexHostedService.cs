using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Scheduling;
using CodeMemory.Indexing;
using CodeMemory.Services;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace CodeMemory.AspNet.Services;

public sealed class RebuildIndexHostedService : BackgroundService
{
    readonly ILogger<RebuildIndexHostedService> logger;
    readonly RepoRegistryService registry;
    readonly IServiceRegistry serviceRegistry;
    readonly IServiceScopeFactory scopeFactory;
    readonly IRepoContextAccessor repoContext;
    readonly IOptions<RebuildOptions> options;
    readonly IndexingOptions indexingOptions;
    readonly SemaphoreSlim gate = new(1, 1);

    public RebuildIndexHostedService(
        ILogger<RebuildIndexHostedService> logger,
        RepoRegistryService registry,
        IServiceRegistry serviceRegistry,
        IServiceScopeFactory scopeFactory,
        IRepoContextAccessor repoContext,
        IOptions<RebuildOptions> options,
        IOptions<IndexingOptions> indexingOptions)
    {
        this.logger = logger;
        this.registry = registry;
        this.serviceRegistry = serviceRegistry;
        this.scopeFactory = scopeFactory;
        this.repoContext = repoContext;
        this.options = options;
        this.indexingOptions = indexingOptions.Value;
    }

    async Task rebuildAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
        {
            logger.LogWarning("Previous rebuild still in progress — skipping this tick");
            return;
        }

        try
        {
            logger.LogInformation("Starting scheduled index rebuild");

            var repos = await registry.ListAsync();

            foreach (var repo in repos)
            {
                if (ct.IsCancellationRequested) break;

                logger.LogInformation("Rebuilding index for '{Repo}'", repo.Name);

                var repoTimeout = TimeSpan.FromMinutes(indexingOptions.RepoTimeoutMinutes);
                using var repoCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                repoCts.CancelAfter(repoTimeout);
                var repoCt = repoCts.Token;

                try
                {
                    if (!string.IsNullOrEmpty(repo.GitUrl))
                        await gitPullAsync(repo, repoCt);

                    var storage = serviceRegistry.GetStorage(repo.Name);
                    await storage.ClearAllAsync(repoCt);
                    IndexingState.MarkIncomplete(repo.Name);

                    repoContext.CurrentRepoName = repo.Name;
                    repoContext.CurrentRepoRoot = repo.LocalPath;

                    var indexResult = await indexWithRetryAsync(repo.Name, repo.LocalPath, repoCt);

                    IndexingState.MarkCompleted(repo.Name);
                    IndexingState.StoreRelationshipCount(repo.Name, indexResult.RelationshipCount);
                    await registry.UpdateIndexStatusAsync(repo.Name, "Indexed");

                    logger.LogInformation("Rebuild complete for '{Repo}'", repo.Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Rebuild failed for '{Repo}'", repo.Name);
                    IndexingState.ClearProgress(repo.Name);
                    await registry.UpdateIndexStatusAsync(repo.Name, "Failed", errorMessage: ex.Message);
                }
                finally
                {
                    repoContext.CurrentRepoName = null;
                    repoContext.CurrentRepoRoot = null;
                }
            }

            logger.LogInformation("Scheduled rebuild finished");
        }
        finally
        {
            gate.Release();
        }
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
                using var scope = scopeFactory.CreateScope();
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

    async Task gitPullAsync(Repositories repo, CancellationToken ct)
    {
        logger.LogInformation("Pulling latest for '{Repo}'", repo.Name);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Value?.GitPullTimeoutSeconds ?? 120));

        var psi = new ProcessStartInfo("git")
        {
            Arguments = "pull --ff-only",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repo.LocalPath,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        await process.WaitForExitAsync(cts.Token);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            logger.LogWarning("git pull for '{Repo}' exited with code {Code}: {Error}",
                repo.Name, process.ExitCode, error);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cron = options.Value?.Cron;
        if (string.IsNullOrWhiteSpace(cron))
        {
            logger.LogDebug("RebuildIndex cron not configured — skipping.");
            return;
        }

        var expression = CronExpression.Parse(cron);
        logger.LogInformation("Rebuild index scheduled with cron '{Cron}'", cron);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = expression.GetNextOccurrence(now);

            if (next is null)
            {
                logger.LogWarning("No future occurrence found for cron '{Cron}'", cron);
                return;
            }

            var delay = next.Value - now;
            logger.LogInformation("Next rebuild at {Next:O} (in {Delay})", next.Value, delay);

            await Task.Delay(delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            await rebuildAsync(stoppingToken);
        }
    }
}
