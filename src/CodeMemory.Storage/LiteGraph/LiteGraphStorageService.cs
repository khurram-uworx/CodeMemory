using LiteGraph;
using LiteGraph.GraphRepositories;
using Microsoft.Extensions.Logging;
using System.Collections.Specialized;

namespace CodeMemory.Storage.LiteGraph;

public record LiteGraphStorageOptions
{
    public Guid TenantGUID { get; init; } = Guid.NewGuid();
    public Guid GraphGUID { get; init; } = Guid.NewGuid();
    public bool Ephemeral { get; init; } = true;
    public string? Filename { get; init; } = null;
}

public sealed class LiteGraphStorageService : IStorageService, IDisposable
{
    readonly string repoRoot;
    readonly LiteGraphClient client;
    readonly LiteGraphStorageOptions options;
    readonly ILogger<LiteGraphStorageService>? logger;

    Guid tenantGuid;
    Guid graphGuid;
    bool initialized;
    bool disposed;

    public LiteGraphStorageService(
        string repoRoot,
        LiteGraphStorageOptions? options = null,
        ILogger<LiteGraphStorageService>? logger = null)
    {
        this.repoRoot = repoRoot;
        this.options = options ?? new LiteGraphStorageOptions();
        this.logger = logger;

        var dbSettings = new DatabaseSettings
        {
            Type = DatabaseTypeEnum.Sqlite,
            InMemory = this.options.Ephemeral,
            Filename = this.options.Filename ?? "litegraph.db"
        };

        var repo = GraphRepositoryFactory.Create(dbSettings);
        client = new LiteGraphClient(repo);
        client.InitializeRepository();
    }

    public string RepoRoot => repoRoot;
    public LiteGraphClient Client => client;
    public Guid TenantGuid => tenantGuid;
    public Guid GraphGuid => graphGuid;

    void throwIfNotInitialized()
    {
        if (!initialized)
            throw new InvalidOperationException("Storage service not initialized. Call InitializeAsync first.");
    }

    async Task<global::LiteGraph.Node?> ResolveNodeBySymbolId(string symbolId, CancellationToken ct)
        => await client.Node.ReadFirst(
            tenantGuid, graphGuid,
            name: "s:" + symbolId,
            token: ct).ConfigureAwait(false);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        tenantGuid = options.TenantGUID;
        graphGuid = options.GraphGUID;

        if (!await client.Tenant.ExistsByGuid(tenantGuid, ct).ConfigureAwait(false))
        {
            await client.Tenant.Create(
                new TenantMetadata { GUID = tenantGuid, Name = "codememory" }, ct)
                .ConfigureAwait(false);
        }

        if (!await client.Graph.ExistsByGuid(tenantGuid, graphGuid, ct).ConfigureAwait(false))
        {
            await client.Graph.Create(
                new Graph
                {
                    TenantGUID = tenantGuid,
                    GUID = graphGuid,
                    Name = "default",
                }, ct)
                .ConfigureAwait(false);
        }

        initialized = true;
        logger?.LogInformation("LiteGraphStorageService initialized with tenant {Tenant}, graph {Graph}",
            tenantGuid, graphGuid);
    }

    public Task<GraphQueryResult> ExecuteQueryAsync(
        string query,
        Dictionary<string, object>? parameters = null,
        int maxResults = 100,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        throwIfNotInitialized();

        var request = new GraphQueryRequest
        {
            Query = query,
            Parameters = parameters ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
            MaxResults = maxResults,
            TimeoutSeconds = timeoutSeconds,
            IncludeProfile = false
        };

        return client.Query.Execute(tenantGuid, graphGuid, request, ct);
    }

    public async Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        const int batchSize = 200;
        logger?.LogInformation("Storing {Count} symbols into LiteGraph", symbols.Count);

        for (int i = 0; i < symbols.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = symbols.Skip(i).Take(batchSize).ToList();

            foreach (var symbol in batch)
            {
                ct.ThrowIfCancellationRequested();

                var existing = await client.Node.ReadFirst(
                    tenantGuid, graphGuid,
                    name: "s:" + symbol.Id,
                    includeData: true,
                    includeSubordinates: true,
                    token: ct).ConfigureAwait(false);

                var node = MappingHelpers.SymbolRecordToNode(symbol, tenantGuid, graphGuid);

                if (existing is not null)
                {
                    node.GUID = existing.GUID;
                    node.CreatedUtc = existing.CreatedUtc;
                    await client.Node.Update(node, ct).ConfigureAwait(false);
                }
                else
                {
                    await client.Node.Create(node, ct).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task StoreChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        const int batchSize = 200;
        logger?.LogInformation("Storing {Count} chunks into LiteGraph", chunks.Count);

        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.Skip(i).Take(batchSize).ToList();

            foreach (var chunk in batch)
            {
                ct.ThrowIfCancellationRequested();

                var existing = await client.Node.ReadFirst(
                    tenantGuid, graphGuid,
                    name: "c:" + chunk.Id,
                    includeData: true,
                    includeSubordinates: true,
                    token: ct).ConfigureAwait(false);

                var node = MappingHelpers.ChunkRecordToNode(chunk, tenantGuid, graphGuid);

                if (existing is not null)
                {
                    node.GUID = existing.GUID;
                    node.CreatedUtc = existing.CreatedUtc;
                    await client.Node.Update(node, ct).ConfigureAwait(false);
                }
                else
                {
                    await client.Node.Create(node, ct).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord> relationships, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        const int batchSize = 200;
        logger?.LogInformation("Storing {Count} relationships into LiteGraph", relationships.Count);

        var nodeGuidCache = new Dictionary<string, Guid>(StringComparer.Ordinal);

        for (int i = 0; i < relationships.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = relationships.Skip(i).Take(batchSize).ToList();

            foreach (var rel in batch)
            {
                ct.ThrowIfCancellationRequested();

                Guid fromGuid;
                if (!nodeGuidCache.TryGetValue(rel.SourceSymbolId, out fromGuid))
                {
                    var fromNode = await ResolveNodeBySymbolId(rel.SourceSymbolId, ct).ConfigureAwait(false);
                    if (fromNode is null)
                    {
                        logger?.LogWarning("Skipping relationship {Id}: source symbol '{Source}' not found",
                            rel.Id, rel.SourceSymbolId);
                        continue;
                    }
                    fromGuid = fromNode.GUID;
                    nodeGuidCache[rel.SourceSymbolId] = fromGuid;
                }

                Guid toGuid;
                if (!nodeGuidCache.TryGetValue(rel.TargetSymbolId, out toGuid))
                {
                    var toNode = await ResolveNodeBySymbolId(rel.TargetSymbolId, ct).ConfigureAwait(false);
                    if (toNode is null)
                    {
                        logger?.LogWarning("Skipping relationship {Id}: target symbol '{Target}' not found",
                            rel.Id, rel.TargetSymbolId);
                        continue;
                    }
                    toGuid = toNode.GUID;
                    nodeGuidCache[rel.TargetSymbolId] = toGuid;
                }

                var existing = await client.Edge.ReadFirst(
                    tenantGuid, graphGuid,
                    name: rel.Id,
                    token: ct).ConfigureAwait(false);

                var edge = MappingHelpers.RelationshipRecordToEdge(
                    rel, tenantGuid, graphGuid, fromGuid, toGuid);

                if (existing is not null)
                {
                    edge.GUID = existing.GUID;
                    edge.CreatedUtc = existing.CreatedUtc;
                    await client.Edge.Update(edge, ct).ConfigureAwait(false);
                }
                else
                {
                    await client.Edge.Create(edge, ct).ConfigureAwait(false);
                }
            }
        }
    }

    // Symbol reads

    public async Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var node = await client.Node.ReadFirst(
            tenantGuid, graphGuid,
            name: "s:" + id,
            includeData: true,
            includeSubordinates: true,
            token: ct).ConfigureAwait(false);
        return node is null ? null : MappingHelpers.NodeToSymbolRecord(node);
    }

    public async Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(
        string filePath, int top = 100, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var tags = new NameValueCollection { ["filePath"] = filePath };
        var results = new List<SymbolRecord>();
        await foreach (var node in client.Node.ReadMany(
            tenantGuid, graphGuid,
            tags: tags,
            includeData: true,
            token: ct).ConfigureAwait(false))
        {
            results.Add(MappingHelpers.NodeToSymbolRecord(node));
            if (results.Count >= top) break;
        }
        return results;
    }

    public async Task<IReadOnlyList<SymbolRecord>> GetSymbolsByKindAsync(
        string kind, int top = 100, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var results = new List<SymbolRecord>();
        await foreach (var node in client.Node.ReadMany(
            tenantGuid, graphGuid,
            labels: new List<string> { kind },
            includeData: true,
            token: ct).ConfigureAwait(false))
        {
            results.Add(MappingHelpers.NodeToSymbolRecord(node));
            if (results.Count >= top) break;
        }
        return results;
    }

    // Relationship reads

    public async Task<RelationshipRecord?> GetRelationshipAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var edge = await client.Edge.ReadFirst(
            tenantGuid, graphGuid,
            name: id,
            includeData: true,
            includeSubordinates: true,
            token: ct).ConfigureAwait(false);
        return edge is null ? null : MappingHelpers.EdgeToRelationshipRecord(edge);
    }

    public async Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsBySourceAsync(
        string sourceSymbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var sourceNode = await client.Node.ReadFirst(
            tenantGuid, graphGuid,
            name: sourceSymbolId,
            token: ct).ConfigureAwait(false);
        if (sourceNode is null) return Array.Empty<RelationshipRecord>();

        var results = new List<RelationshipRecord>();
        await foreach (var edge in client.Edge.ReadEdgesFromNode(
            tenantGuid, graphGuid,
            sourceNode.GUID,
            includeData: true,
            includeSubordinates: true,
            token: ct).ConfigureAwait(false))
        {
            results.Add(MappingHelpers.EdgeToRelationshipRecord(edge));
        }
        return results;
    }

    public async Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsByTargetAsync(
        string targetSymbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var targetNode = await client.Node.ReadFirst(
            tenantGuid, graphGuid,
            name: targetSymbolId,
            token: ct).ConfigureAwait(false);
        if (targetNode is null) return Array.Empty<RelationshipRecord>();

        var results = new List<RelationshipRecord>();
        await foreach (var edge in client.Edge.ReadEdgesToNode(
            tenantGuid, graphGuid,
            targetNode.GUID,
            includeData: true,
            includeSubordinates: true,
            token: ct).ConfigureAwait(false))
        {
            results.Add(MappingHelpers.EdgeToRelationshipRecord(edge));
        }
        return results;
    }

    // Chunk reads

    public async Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var node = await client.Node.ReadFirst(
            tenantGuid, graphGuid,
            name: "c:" + id,
            includeData: true,
            includeSubordinates: true,
            token: ct).ConfigureAwait(false);
        return node is null ? null : MappingHelpers.NodeToChunkRecord(node);
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksBySymbolAsync(
        string symbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var tags = new NameValueCollection { ["symbolId"] = symbolId };
        var results = new List<ChunkRecord>();
        await foreach (var node in client.Node.ReadMany(
            tenantGuid, graphGuid,
            tags: tags,
            includeData: true,
            includeSubordinates: true,
            token: ct).ConfigureAwait(false))
        {
            results.Add(MappingHelpers.NodeToChunkRecord(node));
        }
        return results;
    }

    // Vector search

    public async Task<IReadOnlyList<ScoredChunk>> SearchChunksAsync(
        ReadOnlyMemory<float> embedding, int top = 10, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        var flatEmbedding = new List<float>(embedding.Length);
        for (int i = 0; i < embedding.Length; i++)
            flatEmbedding.Add(embedding.Span[i]);

        var req = new VectorSearchRequest
        {
            TenantGUID = tenantGuid,
            GraphGUID = graphGuid,
            Domain = VectorSearchDomainEnum.Node,
            SearchType = VectorSearchTypeEnum.CosineSimilarity,
            TopK = top,
            Embeddings = flatEmbedding
        };

        var results = new List<ScoredChunk>();
        await foreach (var result in client.Vector.Search(req, ct).ConfigureAwait(false))
        {
            results.Add(MappingHelpers.VectorSearchResultToScoredChunk(result));
        }
        return results;
    }

    // Clear

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        throwIfNotInitialized();
        logger?.LogInformation("Clearing all data from LiteGraph graph {Graph}", graphGuid);

        await client.Edge.DeleteAllInGraph(tenantGuid, graphGuid, ct).ConfigureAwait(false);
        await client.Vector.DeleteAllInGraph(tenantGuid, graphGuid, ct).ConfigureAwait(false);
        await client.Node.DeleteAllInGraph(tenantGuid, graphGuid, ct).ConfigureAwait(false);

        logger?.LogInformation("Cleared all data from LiteGraph graph {Graph}", graphGuid);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
    }
}
