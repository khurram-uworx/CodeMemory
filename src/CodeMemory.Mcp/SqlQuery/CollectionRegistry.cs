using CodeMemory.Storage;
using Microsoft.Extensions.VectorData;

namespace CodeMemory.Mcp.SqlQuery;

/// <summary>Maps a table name to its vector store collection.</summary>
/// <param name="CollectionName">The vector store collection name.</param>
/// <param name="RecordType">The record type stored in the collection.</param>
/// <param name="GetCollection">Function to retrieve the collection from a VectorStore.</param>
public sealed record CollectionEntry(string CollectionName, Type RecordType,
    Func<VectorStore, object> GetCollection);

/// <summary>Registry of known tables and their associated vector store collections.</summary>
public sealed class CollectionRegistry
{
    readonly Dictionary<string, CollectionEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers the default tables: SymbolRecord, ChunkRecord, RelationshipRecord.</summary>
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

    /// <summary>Gets the collection entry for a table name, or null if not found.</summary>
    public CollectionEntry? GetEntry(string tableName)
    {
        entries.TryGetValue(tableName, out var entry);
        return entry;
    }

    /// <summary>Read-only dictionary of all registered entries.</summary>
    public IReadOnlyDictionary<string, CollectionEntry> AllEntries => entries;
}
