using CodeMemory.Indexing.Search;
using CodeMemory.Mcp.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class SemanticSearchTool
{
    readonly ISemanticSearchService? searchService;
    readonly ILogger<SemanticSearchTool> logger;

    public SemanticSearchTool(ILogger<SemanticSearchTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        searchService = serviceProvider.GetService<ISemanticSearchService>();
    }

    [McpServerTool, Description("Searches the indexed repository for code semantically related to the given natural language query. Returns ranked code chunks with file paths and scores.")]
    public async Task<IReadOnlyList<SearchResult>> SemanticSearchAsync(
        [Description("Natural language query describing the code to find")] string query,
        [Description("Maximum number of results to return (default 10, max 50)")] int maxResults = 10,
        [Description("Minimum cosine similarity threshold (0-1, default 0). Higher values return only highly relevant results.")] double minimumSimilarity = 0)
    {
        if (searchService == null)
        {
            logger.LogWarning("Semantic search service not registered — returning empty results");
            return [];
        }

        var capped = Math.Min(maxResults, 50);
        var results = await searchService.SearchAsync(query, capped, minimumSimilarity);
        return results.Select(r => new SearchResult
        {
            ChunkId = r.Chunk.Id,
            FilePath = r.Chunk.FilePath,
            Score = r.Score,
            Content = r.Chunk.Content,
            SymbolName = r.Chunk.SymbolId,
            LineRange = $"{r.Chunk.LineStart}-{r.Chunk.LineEnd}"
        }).ToList();
    }
}
