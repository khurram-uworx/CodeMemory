using Microsoft.Extensions.VectorData;

namespace CodeMemory.Storage.Models;

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
