using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.LiteGraph;
using CodeMemory.Storage;
using LiteGraph.Query;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.AspNet.Tools;

[McpServerToolType]
public sealed class GraphQueryTool
{
    readonly IStorageService? storageService;
    readonly ILogger<GraphQueryTool> logger;

    public GraphQueryTool(ILogger<GraphQueryTool> logger, IServiceProvider serviceProvider)
    {
        this.logger = logger;
        storageService = serviceProvider.GetService<IStorageService>();
    }

    [McpServerTool, Description("Execute a LiteGraph-native graph query (Cypher/GQL-inspired DSL) against the current repository. Returns nodes, edges, rows, and vector search results. Only works with the 'litegraph' storage provider.")]
    public async Task<IDictionary<string, object?>> GraphQueryAsync(
        [Description("LiteGraph query string (e.g. MATCH (n:Class) WHERE n.Name CONTAINS \"Auth\" RETURN n)")] string query,
        [Description("Optional query parameters as key-value pairs")] Dictionary<string, object>? parameters = null,
        [Description("Maximum number of results to return (1-10000, default 100)")] int maxResults = 100,
        [Description("Query timeout in seconds (1-3600, default 30)")] int timeoutSeconds = 30)
    {
        IStorageService? givenStorageService = storageService;
        if (storageService is StorageServiceRouter r)
            givenStorageService = r.GetStorage();

        if (givenStorageService is not LiteGraphStorageService liteGraph)
        {
            logger.LogWarning("GraphQuery attempted without LiteGraph storage provider");
            return new Dictionary<string, object?>
            {
                ["error"] = "graph_query requires the 'litegraph' storage provider. Current provider is not LiteGraph.",
                ["available"] = false
            };
        }

        try
        {
            var result = await liteGraph.ExecuteQueryAsync(
                query, parameters, maxResults, timeoutSeconds).ConfigureAwait(false);

            return new Dictionary<string, object?>
            {
                ["success"] = true,
                ["rowCount"] = result.RowCount,
                ["executionTimeMs"] = result.ExecutionTimeMs,
                ["mutated"] = result.Mutated,
                ["rows"] = result.Rows,
                ["nodes"] = result.Nodes,
                ["edges"] = result.Edges,
                ["labels"] = result.Labels,
                ["warnings"] = result.Warnings,
                ["plan"] = result.Plan is not null ? new
                {
                    kind = result.Plan.Kind.ToString(),
                    mutates = result.Plan.Mutates,
                    usesVectorSearch = result.Plan.UsesVectorSearch,
                    vectorDomain = result.Plan.VectorDomain?.ToString(),
                    hasOrder = result.Plan.HasOrder,
                    hasLimit = result.Plan.HasLimit,
                    estimatedCost = result.Plan.EstimatedCost
                } : null
            };
        }
        catch (GraphQueryParseException ex)
        {
            logger.LogError(ex, "Graph query parse error");
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = $"Query parse error: {ex.Message}",
                ["line"] = ex.Line,
                ["column"] = ex.Column
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Graph query execution failed");
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = $"Query execution failed: {ex.Message}"
            };
        }
    }
}
