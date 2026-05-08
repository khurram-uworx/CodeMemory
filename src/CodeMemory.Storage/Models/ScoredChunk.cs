namespace CodeMemory.Storage.Models;

public sealed class ScoredChunk
{
    public ChunkRecord Chunk { get; init; } = null!;
    public double Score { get; init; }
}
