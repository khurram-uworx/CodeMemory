using System.Collections.Concurrent;
using System.Diagnostics;
using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Registry.Models;
using CodeMemory.Indexing;
using CodeMemory.Services;
using CodeMemory.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CodeMemory.AspNet.Services;

public sealed class CloneIndexService
{
    readonly IDbContextFactory<RepoRegistryDbContext> contextFactory;
    readonly IServiceRegistry storageRegistry;
    readonly IRepoContextAccessor repoContext;
    readonly IServiceScopeFactory scopeFactory;
    readonly IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator;
    readonly ILoggerFactory loggerFactory;
    readonly ILogger<CloneIndexService> logger;
    readonly RepoRegistryOptions registryOptions;
    readonly ConcurrentDictionary<string, bool> inProgress = new(StringComparer.OrdinalIgnoreCase);

    public CloneIndexService(
        IDbContextFactory<RepoRegistryDbContext> contextFactory,
        IServiceRegistry storageRegistry,
        IRepoContextAccessor repoContext,
        IServiceScopeFactory scopeFactory,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILoggerFactory loggerFactory,
        ILogger<CloneIndexService> logger,
        RepoRegistryOptions registryOptions)
    {
        this.contextFactory = contextFactory;
        this.storageRegistry = storageRegistry;
        this.repoContext = repoContext;
        this.scopeFactory = scopeFactory;
        this.embeddingGenerator = embeddingGenerator;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
        this.registryOptions = registryOptions;
    }

    public Task EnqueueRepoAsync(string repoName, string source, string? branch)
    {
        if (!inProgress.TryAdd(repoName, true))
        {
            logger.LogWarning("Repo '{RepoName}' is already being cloned/indexed — skipping duplicate", repoName);
            return Task.CompletedTask;
        }

        var isUrl = source.Contains("://");
        var cloneBasePath = Path.GetFullPath(registryOptions.CloneBasePath);
        var clonePath = isUrl ? Path.GetFullPath(Path.Combine(cloneBasePath, repoName)) : source;

        _ = Task.Run(async () =>
        {
            try
            {
                if (isUrl)
                {
                    await UpdateCloneStatusAsync(repoName, "Cloning");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(registryOptions.CloneTimeoutSeconds));

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

                    await UpdateCloneStatusAsync(repoName, "Cloned", localPath: clonePath);
                }
                else
                    await UpdateCloneStatusAsync(repoName, "Cloned", localPath: clonePath);

                await InitializeAndIndexAsync(repoName, clonePath);
            }
            catch (OperationCanceledException)
            {
                var msg = $"git clone timed out after {registryOptions.CloneTimeoutSeconds} seconds";
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
        var logger = loggerFactory.CreateLogger<StorageService>();
        var storage = new StorageService(repoPath, logger,
            new Memori.Storage.InMemoryVectorStore(), embeddingGenerator);

        storageRegistry.Register(repoName, storage);

        await UpdateIndexStatusAsync(repoName, "Indexing");

        try
        {
            repoContext.CurrentRepoName = repoName;
            repoContext.CurrentRepoRoot = repoPath;

            using var scope = scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
            await engine.RunIndexingAsync(repoPath, CancellationToken.None);

            IndexingState.MarkCompleted(repoName);
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

        if (Directory.Exists(repo.LocalPath) && !string.IsNullOrEmpty(repo.GitUrl))
        {
            try { Directory.Delete(repo.LocalPath, recursive: true); }
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

    async Task<RegisteredRepo?> GetRepoAsync(string name)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
    }
}
