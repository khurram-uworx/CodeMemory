using System.ComponentModel;
using System.Text.Json;
using CodeMemory.Indexing;
using CodeMemory.Services;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

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
    public async Task<string> RescanRepositoryAsync(
        [Description("Optional: skip files matching these patterns (e.g., '**/*.generated.cs,**/bin/**')")] string? excludePatterns = null,
        CancellationToken ct = default)
    {
        try
        {
            var repoRoot = storage.RepoRoot;
            logger.LogInformation("Rescan requested for repo {RepoRoot}", repoRoot);

            IndexingState.MarkIncomplete(repoRoot);

            await storage.ClearAllAsync(ct);

            using var scope = scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
            await engine.RunIndexingAsync(repoRoot, ct);

            IndexingState.MarkCompleted(repoRoot);

            return JsonSerializer.Serialize(new
            {
                status = "ok",
                repoRoot,
                message = "Repository re-indexed successfully"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rescan failed");
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    [McpServerTool, Description("Returns the root path of the currently active repository being indexed and queried.")]
    public string GetRepositoryRoot()
    {
        try
        {
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                repoRoot = storage.RepoRoot
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }
}
