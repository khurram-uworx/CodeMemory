using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using System.Collections.Concurrent;
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
    readonly ConcurrentDictionary<string, ComponentInformation> componentMapping = new(StringComparer.OrdinalIgnoreCase);
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

    public async Task DeleteSymbolsByFileAsync(string filePath, CancellationToken ct = default)
    {
        throwIfNotInitialized();

        Expression<Func<SymbolRecord, bool>> filter = s => s.FilePath == filePath;
        var toDelete = await symbols!.GetAsync(filter, top: 10000, options: null, ct).ToListAsync(ct);
        var ids = toDelete.Select(s => s.Id).ToList();

        if (ids.Count > 0)
            await symbols!.DeleteAsync(ids, ct);
    }

    public async Task DeleteChunksByFileAsync(string filePath, CancellationToken ct = default)
    {
        throwIfNotInitialized();

        Expression<Func<ChunkRecord, bool>> filter = c => c.FilePath == filePath;
        var toDelete = await chunks!.GetAsync(filter, top: 10000, options: null, ct).ToListAsync(ct);
        var ids = toDelete.Select(c => c.Id).ToList();

        if (ids.Count > 0)
            await chunks!.DeleteAsync(ids, ct);
    }

    public async Task DeleteRelationshipsBySourceIdsAsync(IReadOnlyList<string> sourceIds, CancellationToken ct = default)
    {
        throwIfNotInitialized();

        if (sourceIds.Count == 0) return;
        Expression<Func<RelationshipRecord, bool>> filter = r => sourceIds.Contains(r.SourceSymbolId);
        var toDelete = await relationships!.GetAsync(filter, top: 10000, options: null, ct).ToListAsync(ct);
        var ids = toDelete.Select(r => r.Id).ToList();

        if (ids.Count > 0)
            await relationships!.DeleteAsync(ids, ct);
    }

    public async Task DeleteRelationshipsByTargetIdsAsync(IReadOnlyList<string> targetIds, CancellationToken ct = default)
    {
        throwIfNotInitialized();

        if (targetIds.Count == 0) return;
        Expression<Func<RelationshipRecord, bool>> filter = r => targetIds.Contains(r.TargetSymbolId);
        var toDelete = await relationships!.GetAsync(filter, top: 10000, options: null, ct).ToListAsync(ct);
        var ids = toDelete.Select(r => r.Id).ToList();

        if (ids.Count > 0)
            await relationships!.DeleteAsync(ids, ct);
    }

    public async Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        return await symbols!.GetAsync(id, cancellationToken: ct);
    }

    public async Task<SymbolRecord?> GetSymbolByFullNameAsync(string fullName, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        if (string.IsNullOrWhiteSpace(fullName))
            return null;

        Expression<Func<SymbolRecord, bool>> filter = s => s.FullName == fullName;
        var symbol = (await symbols!.GetAsync(filter, top: 1, options: null, ct).ToListAsync(ct)).FirstOrDefault();
        if (symbol != null)
            return symbol;

        // Fallbacks in one deterministic in-memory pass so behavior is identical across backends:
        // 1) signature-insensitive prefix — "Ns.Util.getLikeMethod(string pattern)" <- "Ns.Util.getLikeMethod";
        //    overloads resolve to the first match, documented tradeoff.
        // 2) last-segment — a dotted path on an index with simple FullNames (e.g. built before
        //    package/namespace qualification) resolves via the trailing identifier when it is
        //    unambiguous; ambiguous bare names (Request ×4 across packages) return null so the
        //    caller surfaces the not-found + suggestions diagnostics instead of an arbitrary match.
        var candidates = await symbols!.GetAsync(s => s.FullName != null, top: int.MaxValue, options: null, ct).ToListAsync(ct);
        symbol = candidates.FirstOrDefault(c => c.FullName.StartsWith(SymbolName.SignaturePrefix(fullName), StringComparison.OrdinalIgnoreCase));
        if (symbol != null)
            return symbol;

        var lastSegment = SymbolName.LastSegment(fullName);
        if (lastSegment.Length == 0)
            return null;

        var byShortName = candidates.Where(c => c.Name == lastSegment).ToList();

        return byShortName.Count == 1 ? byShortName[0] : null;
    }

    public async Task<IReadOnlyList<SymbolRecord>> SuggestSymbolsAsync(string query, int top = 5, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        if (string.IsNullOrWhiteSpace(query) || top <= 0)
            return [];

        var candidates = await symbols!.GetAsync(s => s.FullName != null, top: int.MaxValue, options: null, ct).ToListAsync(ct);
        if (candidates.Count == 0)
            return [];

        var lastSegment = SymbolName.LastSegment(query);

        return candidates
            .Where(c => !string.Equals(c.FullName, query, StringComparison.OrdinalIgnoreCase))
            .Where(c => c.FullName.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                || (lastSegment.Length > 0 && c.Name.StartsWith(lastSegment, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();
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

    public async Task<IReadOnlyList<SymbolRecord>> GetSymbolsByParentAsync(
        string parentFullName, CancellationToken ct = default)
    {
        throwIfNotInitialized();

        var prefix = parentFullName + ".";
        Expression<Func<SymbolRecord, bool>> filter = s => s.FullName != null;

        return (await symbols!.GetAsync(filter, top: 10000, options: null, ct).ToListAsync(ct))
            .Where(s => s.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
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

    public async Task<SymbolsByKindResult> GetSymbolsByKindWithCountAsync(
        string kind, int top = 100, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        Expression<Func<SymbolRecord, bool>> filter = s => s.Kind == kind;
        var all = await symbols!.GetAsync(filter, top: int.MaxValue, options: null, ct).ToListAsync(ct);
        return new SymbolsByKindResult(all.Take(top).ToList(), all.Count);
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
        ReadOnlyMemory<float> embedding, int top = 10,
        VectorSearchOptions<ChunkRecord>? options = null, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        if (embedding.Length != actualDimension)
            throw new InvalidOperationException(
                $"Query embedding has dimension {embedding.Length}, " +
                $"but the collection was created with dimension {actualDimension}. " +
                "The embedding generator dimension must match the stored vectors.");

        var results = new List<ScoredChunk>();
        await foreach (var result in chunks!.SearchAsync<ReadOnlyMemory<float>>(
            embedding, top, options: options, ct))
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
                var builder = new System.Data.Common.DbConnectionStringBuilder
                {
                    ConnectionString = dbPath
                };
                if (builder.TryGetValue("Data Source", out var dataSource))
                {
                    var filePath = dataSource?.ToString();
                    if (!string.IsNullOrEmpty(filePath) && filePath != ":memory:")
                    {
                        for (var retry = 0; retry < 3; retry++)
                        {
                            try
                            {
                                if (File.Exists(filePath))
                                    File.Delete(filePath);
                                break;
                            }
                            catch (IOException) when (retry < 2)
                            {
                                Thread.Sleep(200 * (retry + 1));
                            }
                        }
                    }
                }
            }
            symbols = null;
            chunks = null;
            relationships = null;
            initialized = false;
        }, ct);
    }

    public Task StoreComponentMappingAsync(IReadOnlyList<ComponentInformation> components, CancellationToken ct = default)
    {
        componentMapping.Clear();
        foreach (var component in components)
            componentMapping[component.BuildFileDirectory] = component;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ComponentInformation>> LoadComponentMappingAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ComponentInformation>>(
            [.. componentMapping.Values]);
    }

    public void Dispose()
    {
        (this.vectorStore as IDisposable)?.Dispose();
        (symbols as IDisposable)?.Dispose();
        (chunks as IDisposable)?.Dispose();
        (relationships as IDisposable)?.Dispose();
    }
}
