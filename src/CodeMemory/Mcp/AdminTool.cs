//using CodeMemory.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class AdminTool
{
    //readonly IIndexingService? indexingService;
    readonly ILogger<AdminTool> logger;

    public AdminTool(ILogger<AdminTool> logger, IServiceProvider serviceProvider)
    {
        this.logger = logger;
        //indexingService = serviceProvider.GetService<IIndexingService>();
    }

    //[McpServerTool, Description("Triggers a full re-index of the current repository. Clears all stored symbols, chunks, and relationships, then rescans the entire codebase. Use this after git pull, manual file changes, or to recover from a corrupted index.")]
    //public async Task<string> RescanRepositoryAsync(
    //    [Description("Optional: skip files matching these patterns (e.g., '**/*.generated.cs,**/bin/**')")] string? excludePatterns = null)
    //{
    //    if (indexingService == null)
    //    {
    //        logger.LogWarning("Indexing service not available");
    //        return JsonSerializer.Serialize(new { status = "error", message = "Indexing service not available" });
    //    }

    //    try
    //    {
    //        logger.LogInformation("Rescan requested for repo {RepoRoot}", indexingService.RepoRoot);
    //        await indexingService.RescanAsync();
    //        return JsonSerializer.Serialize(new
    //        {
    //            status = "ok",
    //            repoRoot = indexingService.RepoRoot,
    //            message = "Repository re-indexed successfully"
    //        });
    //    }
    //    catch (Exception ex)
    //    {
    //        logger.LogError(ex, "Rescan failed for {RepoRoot}", indexingService.RepoRoot);
    //        return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
    //    }
    //}

    //[McpServerTool, Description("Returns the root path of the currently active repository being indexed and queried.")]
    //public string GetRepositoryRoot()
    //{
    //    if (indexingService == null)
    //        return JsonSerializer.Serialize(new { status = "error", message = "Indexing service not available" });

    //    return JsonSerializer.Serialize(new
    //    {
    //        status = "ok",
    //        repoRoot = indexingService.RepoRoot
    //    });
    //}
}
