using CodeMemory.Indexing.Graph;
using CodeMemory.Services.Architecture;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Services.Graph;

public sealed class DependencyGraphService : IDependencyGraphService
{
    readonly IStorageService storage;
    readonly IComponentResolver componentResolver;
    readonly ILogger<DependencyGraphService> logger;

    public DependencyGraphService(IStorageService storage, IComponentResolver componentResolver,
        ILogger<DependencyGraphService> logger)
    {
        this.storage = storage;
        this.componentResolver = componentResolver;
        this.logger = logger;
    }

    async Task bfsAsync(string symbolId, string direction, int maxDepth,
        HashSet<string> visited, List<DependencyNode> result, int currentDepth,
        CancellationToken ct)
    {
        if (currentDepth >= maxDepth || !visited.Add(symbolId))
            return;

        if (direction is "downstream" or "both")
        {
            var rels = await storage.GetRelationshipsByTargetAsync(symbolId, ct);
            foreach (var rel in rels)
            {
                var srcSymbol = await storage.GetSymbolAsync(rel.SourceSymbolId, ct);
                result.Add(new DependencyNode(
                    srcSymbol?.Name ?? rel.SourceSymbolId,
                    srcSymbol?.FilePath ?? "",
                    srcSymbol?.Kind ?? rel.RelationshipType,
                    srcSymbol != null ? $"{srcSymbol.LineStart}-{srcSymbol.LineEnd}" : "",
                    rel.RelationshipType));
                await bfsAsync(rel.SourceSymbolId, direction, maxDepth, visited, result, currentDepth + 1, ct);
            }
        }

        if (direction is "upstream" or "both")
        {
            var rels = await storage.GetRelationshipsBySourceAsync(symbolId, ct);
            foreach (var rel in rels)
            {
                var tgtSymbol = await storage.GetSymbolAsync(rel.TargetSymbolId, ct);
                result.Add(new DependencyNode(
                    tgtSymbol?.Name ?? rel.TargetSymbolId,
                    tgtSymbol?.FilePath ?? "",
                    tgtSymbol?.Kind ?? rel.RelationshipType,
                    tgtSymbol != null ? $"{tgtSymbol.LineStart}-{tgtSymbol.LineEnd}" : "",
                    rel.RelationshipType));
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

        // Include relationships from child symbols (methods, properties, fields, etc.)
        var childSymbols = await storage.GetSymbolsByParentAsync(symbol.FullName, ct);
        foreach (var child in childSymbols)
            await bfsAsync(child.Id, direction, cappedDepth, visited, result, 0, ct);

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
        var seenRelIds = new HashSet<string>();

        async Task addRelationships(IReadOnlyList<RelationshipRecord> rels)
        {
            foreach (var rel in rels)
            {
                if (!seenRelIds.Add(rel.Id))
                    continue;

                if (relationType is "all" || rel.RelationshipType.Equals(relationType, StringComparison.OrdinalIgnoreCase))
                {
                    var tgtSymbol = await storage.GetSymbolAsync(rel.TargetSymbolId, ct);
                    result.Add(new DependencyNode(
                        tgtSymbol?.Name ?? rel.TargetSymbolId,
                        tgtSymbol?.FilePath ?? "",
                        tgtSymbol?.Kind ?? rel.RelationshipType,
                        tgtSymbol != null ? $"{tgtSymbol.LineStart}-{tgtSymbol.LineEnd}" : "",
                        rel.RelationshipType));
                }
            }
        }

        // Direct relationships on the queried symbol
        var downstream = await storage.GetRelationshipsByTargetAsync(symbol.Id, ct);
        await addRelationships(downstream);

        var upstream = await storage.GetRelationshipsBySourceAsync(symbol.Id, ct);
        await addRelationships(upstream);

        // Include relationships from child symbols (methods, properties, fields, etc.)
        var childSymbols = await storage.GetSymbolsByParentAsync(symbol.FullName, ct);
        foreach (var child in childSymbols)
        {
            downstream = await storage.GetRelationshipsByTargetAsync(child.Id, ct);
            await addRelationships(downstream);

            upstream = await storage.GetRelationshipsBySourceAsync(child.Id, ct);
            await addRelationships(upstream);
        }

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

        // Collect all symbols to check: the symbol itself + its children
        var symbolsToCheck = new List<SymbolRecord> { symbol };
        var childSymbols = await storage.GetSymbolsByParentAsync(symbol.FullName, ct);
        symbolsToCheck.AddRange(childSymbols);

        // Get all inbound relationships for all symbols
        var inboundRels = new List<RelationshipRecord>();
        foreach (var s in symbolsToCheck)
        {
            var rels = await storage.GetRelationshipsByTargetAsync(s.Id, ct);
            inboundRels.AddRange(rels);
        }

        if (inboundRels.Count == 0)
        {
            logger.LogDebug("FindTestCoverageAsync({Symbol}): no inbound relationships found", symbolPath);
            return [];
        }

        // Load component mappings to identify test components
        var components = await storage.LoadComponentMappingAsync(ct);
        var testComponentNames = components
            .Where(c => c.ComponentType == ComponentType.Test)
            .Select(c => c.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (testComponentNames.Count == 0)
        {
            logger.LogDebug("FindTestCoverageAsync({Symbol}): no test components found", symbolPath);
            return [];
        }

        // For each inbound relationship, check if the source belongs to a test component
        var testSourceNames = new HashSet<string>();
        foreach (var rel in inboundRels)
        {
            var srcSymbol = await storage.GetSymbolAsync(rel.SourceSymbolId, ct);
            if (srcSymbol == null || string.IsNullOrEmpty(srcSymbol.FilePath))
                continue;

            var componentName = await componentResolver.GetComponentNameAsync(srcSymbol.FilePath, ct: ct);
            if (componentName != null && testComponentNames.Contains(componentName))
                testSourceNames.Add(srcSymbol.Name);
        }

        logger.LogDebug("FindTestCoverageAsync({Symbol}): {Count} test sources (via component inference)",
            symbolPath, testSourceNames.Count);

        return testSourceNames.ToList();
    }
}
