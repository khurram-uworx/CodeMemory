using CodeMemory.Storage;
using Microsoft.Extensions.VectorData;

namespace CodeMemory.Mcp.SqlQuery;

public sealed record CollectionEntry(string CollectionName, Type RecordType,
    Func<VectorStore, object> GetCollection);

public sealed class CollectionRegistry
{
    readonly Dictionary<string, CollectionEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    public CollectionRegistry()
    {
        register<SymbolRecord>("SymbolRecord", "symbols");
        register<ChunkRecord>("ChunkRecord", "chunks");
        register<RelationshipRecord>("RelationshipRecord", "relationships");
    }

    void register<TRecord>(string tableName, string collectionName) where TRecord : class
    {
        entries[tableName] = new CollectionEntry(collectionName, typeof(TRecord),
            store => store.GetCollection<string, TRecord>(collectionName));
    }

    public CollectionEntry? GetEntry(string tableName)
    {
        entries.TryGetValue(tableName, out var entry);
        return entry;
    }

    public IReadOnlyDictionary<string, CollectionEntry> AllEntries => entries;
}
