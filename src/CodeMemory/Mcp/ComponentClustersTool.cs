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
        [Description("Minimum coupling ratio (0.01-1.0, default 0.3) to consider two components related. Lower values produce larger clusters.")]
        double threshold = 0.3)
    {
        if (clusteringService == null)
        {
            logger.LogWarning("ComponentClusteringService not registered — returning empty");
            return [];
        }

        return await clusteringService.GetClustersAsync(threshold);
    }
}
