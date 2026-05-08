using CodeMemory.Storage.Models;
using Microsoft.Extensions.VectorData;
using System.Linq.Expressions;

namespace CodeMemory.Storage.Services;

public sealed class StorageService : IStorageService, IDisposable
{
    readonly VectorStore vectorStore;
    VectorStoreCollection<string, SymbolRecord>? symbols;
    VectorStoreCollection<string, ChunkRecord>? chunks;
    VectorStoreCollection<string, RelationshipRecord>? relationships;
    bool initialized;

    public StorageService(VectorStore vectorStore)
    {
        this.vectorStore = vectorStore;
    }

    void throwIfNotInitialized()
    {
        if (!initialized)
            throw new InvalidOperationException("Storage service not initialized. Call InitializeAsync first.");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        symbols = vectorStore.GetCollection<string, SymbolRecord>("symbols");
        chunks = vectorStore.GetCollection<string, ChunkRecord>("chunks");
        relationships = vectorStore.GetCollection<string, RelationshipRecord>("relationships");

        await Task.WhenAll(
            symbols.EnsureCollectionExistsAsync(ct),
            chunks.EnsureCollectionExistsAsync(ct),
            relationships.EnsureCollectionExistsAsync(ct));

        initialized = true;
    }

    public async Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await this.symbols!.UpsertAsync(symbols, ct);
    }

    public async Task StoreChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await this.chunks!.UpsertAsync(chunks, ct);
    }

    public async Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord> relationships, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await this.relationships!.UpsertAsync(relationships, ct);
    }

    public async Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        return await symbols!.GetAsync(id, cancellationToken: ct);
    }

    public async Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        return await chunks!.GetAsync(id, new RecordRetrievalOptions { IncludeVectors = true }, ct);
    }

    public async Task<RelationshipRecord?> GetRelationshipAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        return await relationships!.GetAsync(id, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(
        string filePath, int top = 100, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        Expression<Func<SymbolRecord, bool>> filter = s => s.FilePath == filePath;
        return await symbols!.GetAsync(filter, top, options: null, ct).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SymbolRecord>> GetSymbolsByKindAsync(
        string kind, int top = 100, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        Expression<Func<SymbolRecord, bool>> filter = s => s.Kind == kind;
        return await symbols!.GetAsync(filter, top, options: null, ct).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksBySymbolAsync(
        string symbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        Expression<Func<ChunkRecord, bool>> filter = c => c.SymbolId == symbolId;
        return await chunks!.GetAsync(
            filter, top: 1000,
            new FilteredRecordRetrievalOptions<ChunkRecord> { IncludeVectors = true }, ct)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsBySourceAsync(
        string sourceSymbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        Expression<Func<RelationshipRecord, bool>> filter = r => r.SourceSymbolId == sourceSymbolId;
        return await relationships!.GetAsync(filter, top: 1000, options: null, ct).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsByTargetAsync(
        string targetSymbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        Expression<Func<RelationshipRecord, bool>> filter = r => r.TargetSymbolId == targetSymbolId;
        return await relationships!.GetAsync(filter, top: 1000, options: null, ct).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchChunksAsync(
        ReadOnlyMemory<float> embedding, int top = 10, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var results = new List<ScoredChunk>();
        await foreach (var result in chunks!.SearchAsync<ReadOnlyMemory<float>>(
            embedding, top, options: null, ct))
        {
            results.Add(new ScoredChunk { Chunk = result.Record, Score = result.Score ?? 0 });
        }
        return results;
    }

    public void Dispose()
    {
        (symbols as IDisposable)?.Dispose();
        (chunks as IDisposable)?.Dispose();
        (relationships as IDisposable)?.Dispose();
    }
}

static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(
        this IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(ct))
            result.Add(item);
        return result;
    }
}
