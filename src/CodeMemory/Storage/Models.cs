using Microsoft.Extensions.VectorData;

namespace CodeMemory.Storage;

public sealed class ScoredChunk
{
    public ChunkRecord Chunk { get; init; } = null!;
    public double Score { get; init; }
}

public sealed class RelationshipRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceSymbolId { get; set; } = string.Empty;

    [VectorStoreData]
    public string TargetSymbolId { get; set; } = string.Empty;

    [VectorStoreData]
    public string RelationshipType { get; set; } = string.Empty;
}

public sealed class SymbolRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreData]
    public string Name { get; set; } = string.Empty;

    [VectorStoreData]
    public string Kind { get; set; } = string.Empty;

    [VectorStoreData]
    public string FilePath { get; set; } = string.Empty;

    [VectorStoreData]
    public int LineStart { get; set; }

    [VectorStoreData]
    public int LineEnd { get; set; }

    [VectorStoreData]
    public string FullName { get; set; } = string.Empty;

    [VectorStoreData(StorageName = "modifiers")]
    public string? Modifiers { get; set; }

    [VectorStoreData]
    public string? Documentation { get; set; }
}

public sealed class ChunkRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreData]
    public string? SymbolId { get; set; }

    [VectorStoreData]
    public string FilePath { get; set; } = string.Empty;

    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreData]
    public string Language { get; set; } = string.Empty;

    [VectorStoreData]
    public int LineStart { get; set; }

    [VectorStoreData]
    public int LineEnd { get; set; }

    [VectorStoreData(StorageName = "metadata")]
    public string? MetadataJson { get; set; }

    // Dimension is overridden at runtime via VectorSchema.CreateChunkDefinition.
    // This attribute provides the compile-time default (1536) as a fallback.
    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float>? Embedding { get; set; }
}
