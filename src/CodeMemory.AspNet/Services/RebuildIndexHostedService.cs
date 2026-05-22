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
    readonly SemaphoreSlim gate = new(1, 1);

    public RebuildIndexHostedService(
        ILogger<RebuildIndexHostedService> logger,
        RepoRegistryService registry,
        IServiceRegistry serviceRegistry,
        IServiceScopeFactory scopeFactory,
        IRepoContextAccessor repoContext,
        IOptions<RebuildOptions> options)
    {
        this.logger = logger;
        this.registry = registry;
        this.serviceRegistry = serviceRegistry;
        this.scopeFactory = scopeFactory;
        this.repoContext = repoContext;
        this.options = options;
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

                try
                {
                    if (!string.IsNullOrEmpty(repo.GitUrl))
                        await gitPullAsync(repo, ct);

                    var storage = serviceRegistry.GetStorage(repo.Name);
                    await storage.ClearAllAsync(ct);
                    IndexingState.MarkIncomplete(repo.Name);

                    repoContext.CurrentRepoName = repo.Name;
                    repoContext.CurrentRepoRoot = repo.LocalPath;

                    using var scope = scopeFactory.CreateScope();
                    var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
                    await engine.RunIndexingAsync(repo.LocalPath, ct);

                    IndexingState.MarkCompleted(repo.Name);
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

    async Task gitPullAsync(RegisteredRepo repo, CancellationToken ct)
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
