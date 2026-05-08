using CodeMemory.Storage.Models;

namespace CodeMemory.Indexing.Search;

public interface ISemanticSearchService
{
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(string query, int maxResults = 10, double minimumSimilarity = 0, CancellationToken ct = default);
}
