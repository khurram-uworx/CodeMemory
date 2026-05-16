using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using System.Linq.Expressions;

namespace CodeMemory.Storage;

public sealed class StorageService : IStorageService, IDisposable
{
    readonly ILogger<StorageService> logger;
    readonly string repoRoot;
    readonly VectorStore vectorStore;
    readonly IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator;
    readonly int configuredDimension;
    int actualDimension;
    VectorStoreCollection<string, SymbolRecord>? symbols;
    VectorStoreCollection<string, ChunkRecord>? chunks;
    VectorStoreCollection<string, RelationshipRecord>? relationships;
    bool initialized;

    public StorageService(string repoRoot,
        ILogger<StorageService> logger,
        VectorStore vectorStore,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        this.repoRoot = repoRoot;
        this.logger = logger;
        this.vectorStore = vectorStore;
        this.embeddingGenerator = embeddingGenerator;
        this.configuredDimension = configuredDimension;
    }

    void throwIfNotInitialized()
    {
        if (!initialized)
            throw new InvalidOperationException("Storage service not initialized. Call InitializeAsync first.");
    }

    public string RepoRoot => this.repoRoot;

    public VectorStore? Store => vectorStore;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dimension = configuredDimension;
        if (embeddingGenerator?.GetService(typeof(EmbeddingGeneratorMetadata)) is EmbeddingGeneratorMetadata meta
            && meta.DefaultModelDimensions.HasValue)

            dimension = meta.DefaultModelDimensions.Value;

        actualDimension = dimension;

        symbols = vectorStore.GetCollection<string, SymbolRecord>("symbols");
        chunks = vectorStore.GetCollection<string, ChunkRecord>("chunks",
            VectorSchema.CreateChunkDefinition(dimension));
        relationships = vectorStore.GetCollection<string, RelationshipRecord>("relationships");

        await Task.WhenAll(
            symbols.EnsureCollectionExistsAsync(ct),
            chunks.EnsureCollectionExistsAsync(ct),
            relationships.EnsureCollectionExistsAsync(ct));

        initialized = true;
    }

    public async Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord> symbolRecords, CancellationToken ct = default)
    {
        const int batchSize = 200;
        throwIfNotInitialized();
        this.logger.LogInformation("Storing {Count} symbols into the store", symbolRecords.Count);

        for (int i = 0; i < symbolRecords.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = symbolRecords
                .Skip(i)
                .Take(batchSize)
                .ToList();

            // Avoid capturing synchronization context to prevent potential deadlocks
            await this.symbols!.UpsertAsync(batch, ct).ConfigureAwait(false);
        }
    }

    public async Task StoreChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        foreach (var chunk in chunks)
        {
            if (chunk.Embedding.HasValue && chunk.Embedding.Value.Length != actualDimension)
                throw new InvalidOperationException(
                    $"Chunk '{chunk.Id}' has embedding dimension {chunk.Embedding.Value.Length}, " +
                    $"but the collection was created with dimension {actualDimension}. " +
                    "The embedding generator dimension must match the storage schema dimension.");
        }
        await this.chunks!.UpsertAsync(chunks, ct);
    }

    public async Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord> relationships, CancellationToken ct = default)
    {
        const int batchSize = 200;
        throwIfNotInitialized();
        this.logger.LogInformation("Storing {Count} relationships into the store", relationships.Count);

        for (int i = 0; i < relationships.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = relationships
                .Skip(i)
                .Take(batchSize)
                .ToList();

            // Avoid capturing synchronization context to prevent potential deadlocks
            await this.relationships!.UpsertAsync(batch, ct).ConfigureAwait(false);
        }
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
        if (embedding.Length != actualDimension)
            throw new InvalidOperationException(
                $"Query embedding has dimension {embedding.Length}, " +
                $"but the collection was created with dimension {actualDimension}. " +
                "The embedding generator dimension must match the stored vectors.");

        var results = new List<ScoredChunk>();
        await foreach (var result in chunks!.SearchAsync<ReadOnlyMemory<float>>(
            embedding, top, options: null, ct))
        {
            results.Add(new ScoredChunk { Chunk = result.Record, Score = result.Score ?? 0 });
        }
        return results;
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await Task.Run(() =>
        {
            var dbPath = vectorStore.GetType().GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(vectorStore) as string;
            if (!string.IsNullOrEmpty(dbPath))
            {
                var filePath = dbPath!.Replace("Data Source=", "");
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            symbols = null;
            chunks = null;
            relationships = null;
            initialized = false;
        }, ct);
    }

    public void Dispose()
    {
        (symbols as IDisposable)?.Dispose();
        (chunks as IDisposable)?.Dispose();
        (relationships as IDisposable)?.Dispose();
    }
}
