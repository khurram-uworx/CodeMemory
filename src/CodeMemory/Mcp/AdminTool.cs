using CodeMemory.Diagnostics;
using CodeMemory.Indexing;
using CodeMemory.Mcp.Models;
using CodeMemory.Services;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class AdminTool
{
    readonly IStorageService storage;
    readonly IServiceScopeFactory scopeFactory;
    readonly ILogger<AdminTool> logger;

    public AdminTool(IStorageService storage, IServiceScopeFactory scopeFactory, ILogger<AdminTool> logger)
    {
        this.storage = storage;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    [McpServerTool, Description("Triggers a full re-index of the current repository. Clears all stored symbols, chunks, and relationships, then rescans the entire codebase. Use this after git pull, manual file changes, or to recover from a corrupted index.")]
    public async Task<AdminRescanResult> RescanRepositoryAsync(
        [Description("Optional: skip files matching these patterns (e.g., '**/*.generated.cs,**/bin/**')")] string? excludePatterns = null,
        CancellationToken ct = default)
    {
        var rescanRepoName = Path.GetFileName(storage.RepoRoot.TrimEnd(Path.DirectorySeparatorChar));
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "rescan"), new("host", "mcp"), new(CodeMemoryMetrics.Tags.Repo, rescanRepoName));

        var repoRoot = storage.RepoRoot;
        logger.LogInformation("Rescan requested for repo {RepoRoot}", repoRoot);

        IndexingState.MarkIncomplete(repoRoot);

        await storage.ClearAllAsync(ct);

        using var scope = scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
        var result = await engine.RunIndexingAsync(repoRoot, ct);

        IndexingState.MarkCompleted(repoRoot);
        IndexingState.StoreRelationshipCount(repoRoot, result.RelationshipCount);

        return new AdminRescanResult("ok", repoRoot, "Repository re-indexed successfully");
    }

    [McpServerTool, Description("Returns the root path of the currently active repository being indexed and queried.")]
    public AdminRepositoryRootResult GetRepositoryRoot()
    {
        var rootRepoName = Path.GetFileName(storage.RepoRoot.TrimEnd(Path.DirectorySeparatorChar));
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "get_repository_root"), new("host", "mcp"), new(CodeMemoryMetrics.Tags.Repo, rootRepoName));

        return new AdminRepositoryRootResult("ok", storage.RepoRoot);
    }
}
