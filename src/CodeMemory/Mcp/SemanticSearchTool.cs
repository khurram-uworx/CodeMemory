using CodeMemory.Indexing.Search;
using CodeMemory.Mcp.Models;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class SemanticSearchTool
{
    readonly ISemanticSearchService? searchService;
    readonly IStorageService? storage;
    readonly ILogger<SemanticSearchTool> logger;

    public SemanticSearchTool(ILogger<SemanticSearchTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        searchService = serviceProvider.GetService<ISemanticSearchService>();
        storage = serviceProvider.GetService<IStorageService>();
    }

    [McpServerTool, Description("Searches the indexed repository for code and documentation semantically related to the given natural language query. Returns metadata-only results (file paths, scores, line ranges) — no source content. Use the read tool to fetch specific lines from matched files.")]
    public async Task<IReadOnlyList<SearchResult>> SemanticSearchAsync(
        [Description("Natural language query describing the code or documentation to find")] string query,
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

        var output = new List<SearchResult>(results.Count);
        foreach (var r in results)
        {
            var symbolName = r.Chunk.SymbolId;
            if (storage != null && r.Chunk.SymbolId != null)
            {
                var symbol = await storage.GetSymbolAsync(r.Chunk.SymbolId);
                if (symbol != null)
                    symbolName = symbol.Name;
            }

            output.Add(new SearchResult
            {
                ChunkId = r.Chunk.Id,
                FilePath = r.Chunk.FilePath,
                Score = r.Score,
                SymbolName = symbolName,
                LineRange = $"{r.Chunk.LineStart}-{r.Chunk.LineEnd}",
                LineStart = r.Chunk.LineStart,
                LineEnd = r.Chunk.LineEnd
            });
        }

        return output;
    }
}
