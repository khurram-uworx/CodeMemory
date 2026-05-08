using Microsoft.Extensions.VectorData;

namespace CodeMemory.Storage.Models;

public sealed class ChunkRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreData]
    public string SymbolId { get; set; } = string.Empty;

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

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float>? Embedding { get; set; }
}
