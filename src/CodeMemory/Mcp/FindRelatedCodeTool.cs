using CodeMemory.Indexing.Graph;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class FindRelatedCodeTool
{
    readonly IDependencyGraphService? graphService;
    readonly ILogger<FindRelatedCodeTool> logger;

    public FindRelatedCodeTool(ILogger<FindRelatedCodeTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        graphService = serviceProvider.GetService<IDependencyGraphService>();
    }

    [McpServerTool, Description("Finds code related to a given symbol by traversing dependency relationships. Returns related symbols grouped by relation type.")]
    public async Task<IReadOnlyList<DependencyNode>> FindRelatedCodeAsync(
        [Description("Qualified symbol name to find related code for")] string symbolPath,
        [Description("Filter by relation type: 'all', 'calls', 'references', 'inherits', 'implements'")] string relationType = "all")
    {
        if (graphService == null)
        {
            logger.LogWarning("Dependency graph service not registered — returning empty result");
            return [];
        }

        var related = await graphService.FindRelatedAsync(symbolPath, relationType);
        logger.LogDebug("FindRelatedCodeAsync({Symbol}, {Type}): {Count} results",
            symbolPath, relationType, related.Count);
        return related;
    }
}
