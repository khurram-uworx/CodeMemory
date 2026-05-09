using CodeMemory.Indexing.Git;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class GitHistoryTool
{
    readonly IGitHistoryService? gitHistoryService;
    readonly ILogger<GitHistoryTool> logger;

    public GitHistoryTool(ILogger<GitHistoryTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        gitHistoryService = serviceProvider.GetService<IGitHistoryService>();
    }

    [McpServerTool, Description("Returns git commit history for a given symbol. Shows total commits, unique authors, first/last commit dates, and recent commits.")]
    public async Task<object?> GetSymbolHistoryAsync(
        [Description("Full symbol path (e.g., MyClass.MyMethod)")] string symbolPath,
        [Description("Maximum number of commits to return (default 20)")] int maxCommits = 20)
    {
        if (gitHistoryService == null)
        {
            logger.LogWarning("GitHistoryService not registered — returning null");
            return new { warning = "Git history service not available", symbolPath };
        }

        return await gitHistoryService.GetSymbolHistoryAsync(symbolPath, maxCommits);
    }

    [McpServerTool, Description("Returns the most frequently changed files in the repository (hotspots). Files are ranked by commit count.")]
    public async Task<IReadOnlyList<HotspotInfo>> GetHotspotsAsync(
        [Description("Number of top hotspots to return (default 10)")] int top = 10,
        [Description("Maximum commits to scan (default 100)")] int maxCommits = 100)
    {
        if (gitHistoryService == null)
        {
            logger.LogWarning("GitHistoryService not registered — returning empty");
            return [];
        }

        return await gitHistoryService.GetHotspotsAsync(top, maxCommits);
    }
}
