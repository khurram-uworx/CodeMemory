using CodeMemory.Storage;
using Microsoft.Extensions.VectorData;

namespace CodeMemory.AspNet.Configuration;

public sealed class StorageServiceRouter : IStorageService
{
    readonly IServiceRegistry registry;
    readonly IRepoContextAccessor repoContext;

    public StorageServiceRouter(IServiceRegistry registry, IRepoContextAccessor repoContext)
    {
        this.registry = registry;
        this.repoContext = repoContext;
    }

    public string RepoRoot => registry.GetStorage(repoContext.CurrentRepoName).RepoRoot;

    public VectorStore? Store => GetStorage().Store;

    public IStorageService GetStorage()
        => registry.GetStorage(repoContext.CurrentRepoName);

    public Task InitializeAsync(CancellationToken ct = default)
        => GetStorage().InitializeAsync(ct);

    public Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken ct = default)
        => GetStorage().StoreSymbolsAsync(symbols, ct);

    public Task StoreChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
        => GetStorage().StoreChunksAsync(chunks, ct);

    public Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord> relationships, CancellationToken ct = default)
        => GetStorage().StoreRelationshipsAsync(relationships, ct);

    public Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken ct = default)
        => GetStorage().GetSymbolAsync(id, ct);

    public Task<SymbolRecord?> GetSymbolByFullNameAsync(string fullName, CancellationToken ct = default)
        => GetStorage().GetSymbolByFullNameAsync(fullName, ct);

    public Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken ct = default)
        => GetStorage().GetChunkAsync(id, ct);

    public Task<RelationshipRecord?> GetRelationshipAsync(string id, CancellationToken ct = default)
        => GetStorage().GetRelationshipAsync(id, ct);

    public Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(string filePath, int top = 100, CancellationToken ct = default)
        => GetStorage().GetSymbolsByFileAsync(filePath, top, ct);

    public Task<IReadOnlyList<SymbolRecord>> GetSymbolsByKindAsync(string kind, int top = 100, CancellationToken ct = default)
        => GetStorage().GetSymbolsByKindAsync(kind, top, ct);

    public Task<IReadOnlyList<SymbolRecord>> GetSymbolsByParentAsync(string parentFullName, CancellationToken ct = default)
        => GetStorage().GetSymbolsByParentAsync(parentFullName, ct);

    public Task<IReadOnlyList<ChunkRecord>> GetChunksBySymbolAsync(string symbolId, CancellationToken ct = default)
        => GetStorage().GetChunksBySymbolAsync(symbolId, ct);

    public Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsBySourceAsync(string sourceSymbolId, CancellationToken ct = default)
        => GetStorage().GetRelationshipsBySourceAsync(sourceSymbolId, ct);

    public Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsByTargetAsync(string targetSymbolId, CancellationToken ct = default)
        => GetStorage().GetRelationshipsByTargetAsync(targetSymbolId, ct);

    public Task<IReadOnlyList<ScoredChunk>> SearchChunksAsync(ReadOnlyMemory<float> embedding, int top = 10, VectorSearchOptions<ChunkRecord>? options = null, CancellationToken ct = default)
        => GetStorage().SearchChunksAsync(embedding, top, options, ct);

    public Task DeleteSymbolsByFileAsync(string filePath, CancellationToken ct = default)
        => GetStorage().DeleteSymbolsByFileAsync(filePath, ct);

    public Task DeleteChunksByFileAsync(string filePath, CancellationToken ct = default)
        => GetStorage().DeleteChunksByFileAsync(filePath, ct);

    public Task DeleteRelationshipsBySourceIdsAsync(IReadOnlyList<string> sourceIds, CancellationToken ct = default)
        => GetStorage().DeleteRelationshipsBySourceIdsAsync(sourceIds, ct);

    public Task DeleteRelationshipsByTargetIdsAsync(IReadOnlyList<string> targetIds, CancellationToken ct = default)
        => GetStorage().DeleteRelationshipsByTargetIdsAsync(targetIds, ct);

    public Task ClearAllAsync(CancellationToken ct = default)
        => GetStorage().ClearAllAsync(ct);
}
