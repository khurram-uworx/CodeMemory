using CodeMemory.Indexing.Architecture;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class ArchitectureOverviewTool
{
    readonly IArchitectureService? architectureService;
    readonly ILogger<ArchitectureOverviewTool> logger;

    public ArchitectureOverviewTool(ILogger<ArchitectureOverviewTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        architectureService = serviceProvider.GetService<IArchitectureService>();
    }

    [McpServerTool, Description("Returns a high-level overview of the repository structure: top-level components, language breakdown, file counts, and symbol counts.")]
    public async Task<ArchitectureOverview> GetArchitectureOverviewAsync(
        [Description("Optional subdirectory to focus on")] string? path = null)
    {
        if (architectureService == null)
        {
            logger.LogWarning("Architecture service not registered — returning default overview");
            return new ArchitectureOverview([], new Dictionary<string, int>(), 0, 0);
        }

        return await architectureService.GetOverviewAsync(path);
    }
}
