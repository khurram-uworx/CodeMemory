using CodeMemory.Diagnostics;
using CodeMemory.Indexing.Architecture;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class ComponentClustersTool
{
    readonly IComponentClusteringService? clusteringService;
    readonly ILogger<ComponentClustersTool> logger;

    public ComponentClustersTool(ILogger<ComponentClustersTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        clusteringService = serviceProvider.GetService<IComponentClusteringService>();
    }

    [McpServerTool, Description("Groups top-level components into logical clusters based on inter-component dependency density. Uses threshold-based coupling analysis from stored symbol relationships.")]
    public async Task<IReadOnlyList<ComponentCluster>> GetComponentClustersAsync(
        [Description("Minimum coupling ratio (0.01-1.0, default 0.3, or as configured in .codememory.json) to consider two components related. Lower values produce larger clusters.")]
        double? threshold = null,
        [Description("Directory depth for fallback component resolution when no project files are found (default 1)")] int depth = 1)
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "get_component_clusters"), new("host", "mcp"));

        if (clusteringService == null)
        {
            logger.LogWarning("ComponentClusteringService not registered — returning empty");
            return [];
        }

        return await clusteringService.GetClustersAsync(threshold, depth);
    }
}
