using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.Indexing;
using CodeMemory.Services;
using CodeMemory.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace CodeMemory.AspNet.Services;

public sealed class IndexingHostedService : BackgroundService
{
    readonly IServiceProvider serviceProvider;
    readonly IRepoContextAccessor repoContext;
    readonly ILogger<IndexingHostedService> logger;

    public IndexingHostedService(IServiceProvider serviceProvider,
        IRepoContextAccessor repoContext, ILogger<IndexingHostedService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.repoContext = repoContext;
        this.logger = logger;
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

        if (pendingRepos.Count == 0)
        {
            logger.LogInformation("No pending repos to index");
            return;
        }

        foreach (var repo in pendingRepos)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                if (repo.CloneStatus == "Pending" && !string.IsNullOrEmpty(repo.GitUrl))
                {
                    logger.LogInformation("Cloning repository '{Name}' from {Url}", repo.Name, repo.GitUrl);

                    await UpdateCloneStatusAsync(dbFactory, repo.Name, "Cloning", ct: stoppingToken);

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

                    await process.WaitForExitAsync(stoppingToken);
                    if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync(stoppingToken);
                        throw new InvalidOperationException($"git clone failed: {error}");
                    }
                }

                if (repo.CloneStatus != "Cloned")
                {
                    await UpdateCloneStatusAsync(dbFactory, repo.Name, "Cloned", localPath: repo.LocalPath, ct: stoppingToken);
                }

                // Initialize storage if not already registered
                try
                {
                    registry.GetStorage(repo.Name);
                }
                catch
                {
                    var embeddingGenerator = serviceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
                    var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                    var storage = new StorageService(repo.LocalPath,
                        loggerFactory.CreateLogger<StorageService>(),
                        new Memori.Storage.InMemoryVectorStore(), embeddingGenerator);
                    registry.Register(repo.Name, storage);
                }

                await UpdateIndexStatusAsync(dbFactory, repo.Name, "Indexing", ct: stoppingToken);

                repoContext.CurrentRepoName = repo.Name;
                repoContext.CurrentRepoRoot = repo.LocalPath;

                using var scope = serviceProvider.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
                await engine.RunIndexingAsync(repo.LocalPath, stoppingToken);

                IndexingState.MarkCompleted(repo.Name);
                await UpdateIndexStatusAsync(dbFactory, repo.Name, "Indexed", ct: stoppingToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Indexing cancelled for repository '{Name}'", repo.Name);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing repository '{Name}'", repo.Name);
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
