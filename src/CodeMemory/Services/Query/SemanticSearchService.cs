using CodeMemory.Indexing.Search;
using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Numerics.Tensors;

namespace CodeMemory.Services.Query;

public sealed class SemanticSearchService : ISemanticSearchService
{
    readonly IStorageService storage;
    readonly IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator;
    readonly ILogger<SemanticSearchService> logger;

    public SemanticSearchService(IStorageService storage,
        ILogger<SemanticSearchService> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        this.storage = storage;
        this.logger = logger;
        this.embeddingGenerator = embeddingGenerator;
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        string query, int maxResults = 10, double minimumSimilarity = 0, CancellationToken ct = default) =>
        await SearchByTextAsync(query, maxResults, minimumSimilarity, ct);

    public async Task<IReadOnlyList<ScoredChunk>> SearchByTextAsync(
        string query, int top = 10, double minimumSimilarity = 0, CancellationToken ct = default)
    {
        if (embeddingGenerator == null)
        {
            logger.LogWarning("No embedding generator registered — cannot perform semantic search");
            return [];
        }

        var embeddings = await embeddingGenerator.GenerateAsync([query], cancellationToken: ct);
        var vector = embeddings[0].Vector;
        var norm = TensorPrimitives.Norm(vector.Span);
        var normalized = new float[vector.Length];
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++)
                normalized[i] = vector.Span[i] / norm;
        }

        logger.LogDebug("Semantic search for \"{Query}\" — embedding dimension {Dim}", query, normalized.Length);
        var results = await storage.SearchChunksAsync(normalized, top, options: null, ct);

        if (minimumSimilarity > 0)
        {
            results = results
                .Where(r => r.Score <= 1.0 - minimumSimilarity)
                .ToList();
        }

        return results;
    }

    public Task<IReadOnlyList<ScoredChunk>> SearchByVectorAsync(
        ReadOnlyMemory<float> vector, int top = 10, CancellationToken ct = default)
    {
        return storage.SearchChunksAsync(vector, top, options: null, ct);
    }
}
