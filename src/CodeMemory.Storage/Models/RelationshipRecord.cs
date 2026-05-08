using Microsoft.Extensions.VectorData;

namespace CodeMemory.Storage.Models;

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
