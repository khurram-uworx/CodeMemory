namespace CodeMemory.Mcp.Models;

public sealed class SearchResult
{
    public string ChunkId { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public double Score { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? SymbolName { get; init; }
    public string LineRange { get; init; } = string.Empty;
}
