using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Scheduling;
using CodeMemory.Indexing;
using CodeMemory.Services;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;

namespace CodeMemory.AspNet.Services;

public sealed class RebuildIndexHostedService : BackgroundService
{
    readonly ILogger<RebuildIndexHostedService> logger;
    readonly RepoRegistryService registry;
    readonly IServiceRegistry serviceRegistry;
    readonly IServiceScopeFactory scopeFactory;
    readonly IRepoContextAccessor repoContext;
    readonly RepoRegistryOptions registryOptions;
    readonly IndexingOptions indexingOptions;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly TimeSpan pollInterval;

    static readonly string SchedulePath = Path.Combine("App_Data", "rebuild-schedule.json");
    CronExpression? expression;

    record ScheduleData(DateTimeOffset NextRun);

    public RebuildIndexHostedService(
        ILogger<RebuildIndexHostedService> logger,
        RepoRegistryService registry,
        IServiceRegistry serviceRegistry,
        IServiceScopeFactory scopeFactory,
        IRepoContextAccessor repoContext,
        RepoRegistryOptions registryOptions,
        IOptions<IndexingOptions> indexingOptions)
    {
        this.logger = logger;
        this.registry = registry;
        this.serviceRegistry = serviceRegistry;
        this.scopeFactory = scopeFactory;
        this.repoContext = repoContext;
        this.registryOptions = registryOptions;
        this.indexingOptions = indexingOptions.Value;
        this.pollInterval = TimeSpan.FromSeconds(registryOptions.RebuildPollIntervalSeconds);
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
        cts.CancelAfter(TimeSpan.FromSeconds(registryOptions.GitCommandTimeoutSeconds));

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

    async Task<bool> IsScheduleDueAsync(CancellationToken ct)
    {
        if (!File.Exists(SchedulePath))
            return false;

        var json = await File.ReadAllTextAsync(SchedulePath, ct);
        var data = JsonSerializer.Deserialize<ScheduleData>(json);
        return data is not null && DateTimeOffset.UtcNow >= data.NextRun;
    }

    async Task PersistNextRunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var next = expression!.GetNextOccurrence(now);
        if (next is null)
        {
            logger.LogWarning("No future occurrence found for cron '{Cron}'", registryOptions.RebuildCron);
            return;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(SchedulePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var data = new ScheduleData(next.Value);
        var json = JsonSerializer.Serialize(data);
        await File.WriteAllTextAsync(SchedulePath, json, ct);
    }

    async Task EnsureFirstScheduleAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(SchedulePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await PersistNextRunAsync(ct);

        // Read back to log the scheduled time
        if (File.Exists(SchedulePath))
        {
            var json = await File.ReadAllTextAsync(SchedulePath, ct);
            var data = JsonSerializer.Deserialize<ScheduleData>(json);
            if (data is not null)
                logger.LogInformation("Next rebuild at {Next:O}", data.NextRun);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cron = registryOptions.RebuildCron;
        if (string.IsNullOrWhiteSpace(cron))
        {
            logger.LogDebug("RebuildIndex cron not configured — skipping.");
            return;
        }

        expression = CronExpression.Parse(cron);
        logger.LogInformation("Rebuild index scheduled with cron '{Cron}', polling every {Poll}s",
            cron, pollInterval.TotalSeconds);

        // Let IndexingHostedService finish first-time indexing before we start polling
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        if (!File.Exists(SchedulePath))
            await EnsureFirstScheduleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await IsScheduleDueAsync(stoppingToken))
            {
                if (await IndexingState.RebuildGate.WaitAsync(TimeSpan.Zero, stoppingToken))
                {
                    try
                    {
                        await rebuildAsync(stoppingToken);
                    }
                    finally
                    {
                        IndexingState.RebuildGate.Release();
                    }
                }
                else
                {
                    logger.LogWarning("Indexing service active — skipping this rebuild tick");
                }

                await PersistNextRunAsync(stoppingToken);
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }
}
