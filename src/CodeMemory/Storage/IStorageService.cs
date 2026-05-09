namespace CodeMemory.Storage;

public interface IStorageService
{
    Task InitializeAsync(CancellationToken ct = default);

    Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken ct = default);

    Task StoreChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default);

    Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord> relationships, CancellationToken ct = default);

    Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken ct = default);

    Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken ct = default);

    Task<RelationshipRecord?> GetRelationshipAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(string filePath, int top = 100, CancellationToken ct = default);

    Task<IReadOnlyList<SymbolRecord>> GetSymbolsByKindAsync(string kind, int top = 100, CancellationToken ct = default);

    Task<IReadOnlyList<ChunkRecord>> GetChunksBySymbolAsync(string symbolId, CancellationToken ct = default);

    Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsBySourceAsync(string sourceSymbolId, CancellationToken ct = default);

    Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsByTargetAsync(string targetSymbolId, CancellationToken ct = default);

    Task<IReadOnlyList<ScoredChunk>> SearchChunksAsync(ReadOnlyMemory<float> embedding, int top = 10, CancellationToken ct = default);

    Task ClearAllAsync(CancellationToken ct = default);
}
