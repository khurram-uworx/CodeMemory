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
        var symbol = await storage.GetSymbolByFullNameAsync(symbolPath, ct);
        if (symbol == null)
        {
            logger.LogDebug("TraceAsync({Symbol}): symbol not found", symbolPath);
            return [];
        }

        var cappedDepth = Math.Clamp(depth, 1, 3);
        var visited = new HashSet<string>();
        var result = new List<DependencyNode>();

        await bfsAsync(symbol.Id, direction, cappedDepth, visited, result, 0, ct);

        logger.LogDebug("TraceAsync({Symbol}, {Direction}, {Depth}): {Count} nodes",
            symbolPath, direction, depth, result.Count);
        return result;
    }

    public async Task<IReadOnlyList<DependencyNode>> FindRelatedAsync(
        string symbolPath, string relationType, CancellationToken ct = default)
    {
        var symbol = await storage.GetSymbolByFullNameAsync(symbolPath, ct);
        if (symbol == null)
        {
            logger.LogDebug("FindRelatedAsync({Symbol}): symbol not found", symbolPath);
            return [];
        }

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

        var downstream = await storage.GetRelationshipsByTargetAsync(symbol.Id, ct);
        await addRelationships(downstream);

        var upstream = await storage.GetRelationshipsBySourceAsync(symbol.Id, ct);
        await addRelationships(upstream);

        logger.LogDebug("FindRelatedAsync({Symbol}, {Type}): {Count} nodes",
            symbolPath, relationType, result.Count);
        return result;
    }

    public async Task<IReadOnlyList<string>> FindTestCoverageAsync(
        string symbolPath, CancellationToken ct = default)
    {
        var symbol = await storage.GetSymbolByFullNameAsync(symbolPath, ct);
        if (symbol == null)
        {
            logger.LogDebug("FindTestCoverageAsync({Symbol}): symbol not found", symbolPath);
            return [];
        }

        var related = await storage.GetRelationshipsByTargetAsync(symbol.Id, ct);
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

        var downstream = await storage.GetRelationshipsBySourceAsync(symbol.Id, ct);
        var relatedFiles = new HashSet<string>();

        foreach (var rel in downstream)
        {
            var relSymbol = await storage.GetSymbolAsync(rel.TargetSymbolId, ct);
            if (relSymbol != null && relSymbol.FilePath.Length > 0)
                relatedFiles.Add(relSymbol.FilePath);
        }

        foreach (var rel in related)
        {
            var relSymbol = await storage.GetSymbolAsync(rel.SourceSymbolId, ct);
            if (relSymbol != null && relSymbol.FilePath.Length > 0)
                relatedFiles.Add(relSymbol.FilePath);
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
