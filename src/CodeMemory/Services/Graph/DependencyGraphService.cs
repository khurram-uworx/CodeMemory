using CodeMemory.Indexing.Graph;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Services.Graph;

public sealed class DependencyGraphService : IDependencyGraphService
{
    readonly IStorageService storage;
    readonly ILogger<DependencyGraphService> logger;

    public DependencyGraphService(IStorageService storage, ILogger<DependencyGraphService> logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    async Task bfsAsync(string symbolId, string direction, int maxDepth,
        HashSet<string> visited, List<DependencyNode> result, int currentDepth,
        CancellationToken ct)
    {
        if (currentDepth >= maxDepth || !visited.Add(symbolId))
            return;

        IReadOnlyList<RelationshipRecord> rels;

        if (direction is "downstream" or "both")
        {
            rels = await storage.GetRelationshipsByTargetAsync(symbolId, ct);
            foreach (var rel in rels)
            {
                result.Add(new DependencyNode(
                    rel.SourceSymbolId, "", rel.RelationshipType, "", rel.RelationshipType));
                await bfsAsync(rel.SourceSymbolId, direction, maxDepth, visited, result, currentDepth + 1, ct);
            }
        }

        if (direction is "upstream" or "both")
        {
            rels = await storage.GetRelationshipsBySourceAsync(symbolId, ct);
            foreach (var rel in rels)
            {
                result.Add(new DependencyNode(
                    rel.TargetSymbolId, "", rel.RelationshipType, "", rel.RelationshipType));
                await bfsAsync(rel.TargetSymbolId, direction, maxDepth, visited, result, currentDepth + 1, ct);
            }
        }
    }

    public async Task<IReadOnlyList<DependencyNode>> TraceAsync(
        string symbolPath, string direction, int depth, CancellationToken ct = default)
    {
        var cappedDepth = Math.Clamp(depth, 1, 3);
        var visited = new HashSet<string>();
        var result = new List<DependencyNode>();

        await bfsAsync(symbolPath, direction, cappedDepth, visited, result, 0, ct);

        logger.LogDebug("TraceAsync({Symbol}, {Direction}, {Depth}): {Count} nodes",
            symbolPath, direction, depth, result.Count);
        return result;
    }

    public async Task<IReadOnlyList<DependencyNode>> FindRelatedAsync(
        string symbolPath, string relationType, CancellationToken ct = default)
    {
        var result = new List<DependencyNode>();

        async Task addRelationships(IReadOnlyList<RelationshipRecord> rels)
        {
            foreach (var rel in rels)
            {
                if (relationType is "all" || rel.RelationshipType.Equals(relationType, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new DependencyNode(
                        rel.TargetSymbolId, "", rel.RelationshipType, "", rel.RelationshipType));
                }
            }
        }

        var downstream = await storage.GetRelationshipsByTargetAsync(symbolPath, ct);
        await addRelationships(downstream);

        var upstream = await storage.GetRelationshipsBySourceAsync(symbolPath, ct);
        await addRelationships(upstream);

        logger.LogDebug("FindRelatedAsync({Symbol}, {Type}): {Count} nodes",
            symbolPath, relationType, result.Count);
        return result;
    }

    public async Task<IReadOnlyList<string>> FindTestCoverageAsync(
        string symbolPath, CancellationToken ct = default)
    {
        var related = await storage.GetRelationshipsByTargetAsync(symbolPath, ct);
        var testSources = related
            .Where(r => r.RelationshipType == "TestCoverage")
            .Select(r => r.SourceSymbolId)
            .Distinct()
            .ToList();

        if (testSources.Count > 0)
        {
            logger.LogDebug("FindTestCoverageAsync({Symbol}): {Count} test sources (from stored relationships)",
                symbolPath, testSources.Count);
            return testSources;
        }

        var downstream = await storage.GetRelationshipsBySourceAsync(symbolPath, ct);
        var relatedFiles = new HashSet<string>();

        foreach (var rel in downstream)
        {
            var symbol = await storage.GetSymbolAsync(rel.TargetSymbolId, ct);
            if (symbol != null && symbol.FilePath.Length > 0)
                relatedFiles.Add(symbol.FilePath);
        }

        foreach (var rel in related)
        {
            var symbol = await storage.GetSymbolAsync(rel.SourceSymbolId, ct);
            if (symbol != null && symbol.FilePath.Length > 0)
                relatedFiles.Add(symbol.FilePath);
        }

        var conventionTests = relatedFiles
            .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileNameWithoutExtension(f).EndsWith("Test", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        logger.LogDebug("FindTestCoverageAsync({Symbol}): {Count} test files (by convention)",
            symbolPath, conventionTests.Count);
        return conventionTests;
    }
}
