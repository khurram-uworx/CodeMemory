using Microsoft.Extensions.VectorData;

namespace CodeMemory.Mcp.SqlQuery;

public sealed class RelationshipWithNamesRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceSymbolId { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceName { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceKind { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceFullName { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourceFilePath { get; set; } = string.Empty;

    [VectorStoreData]
    public string TargetSymbolId { get; set; } = string.Empty;

    [VectorStoreData]
    public string TargetName { get; set; } = string.Empty;

    [VectorStoreData]
    public string TargetKind { get; set; } = string.Empty;

    [VectorStoreData]
    public string TargetFullName { get; set; } = string.Empty;

    [VectorStoreData]
    public string TargetFilePath { get; set; } = string.Empty;

    [VectorStoreData]
    public string RelationshipType { get; set; } = string.Empty;
}

public sealed class SymbolReferenceStatsRecord
{
    [VectorStoreKey]
    public string SymbolId { get; set; } = string.Empty;

    [VectorStoreData]
    public string Name { get; set; } = string.Empty;

    [VectorStoreData]
    public string Kind { get; set; } = string.Empty;

    [VectorStoreData]
    public string FullName { get; set; } = string.Empty;

    [VectorStoreData]
    public string FilePath { get; set; } = string.Empty;

    [VectorStoreData]
    public long IncomingReferences { get; set; }

    [VectorStoreData]
    public long OutgoingReferences { get; set; }

    [VectorStoreData]
    public long IncomingCalls { get; set; }

    [VectorStoreData]
    public long OutgoingCalls { get; set; }

    [VectorStoreData]
    public long IncomingReferencesNonCall { get; set; }

    [VectorStoreData]
    public long OutgoingReferencesNonCall { get; set; }

    [VectorStoreData]
    public long IncomingInherits { get; set; }

    [VectorStoreData]
    public long IncomingImplements { get; set; }

    [VectorStoreData]
    public long IncomingTestCoverage { get; set; }

    [VectorStoreData]
    public long OutgoingInherits { get; set; }

    [VectorStoreData]
    public long OutgoingImplements { get; set; }

    [VectorStoreData]
    public long OutgoingTestCoverage { get; set; }
}
