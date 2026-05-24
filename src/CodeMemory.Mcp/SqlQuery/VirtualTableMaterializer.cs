using CodeMemory.Storage;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace CodeMemory.Mcp.SqlQuery;

public sealed class VirtualTableMaterializer
{
    readonly CollectionRegistry registry;
    readonly ILogger<VirtualTableMaterializer> logger;
    Task? currentMaterialization;
    readonly object gate = new();

    public VirtualTableMaterializer(
        CollectionRegistry registry,
        ILogger<VirtualTableMaterializer> logger)
    {
        this.registry = registry;
        this.logger = logger;
    }

    public Task MaterializeAsync(VectorStore store, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (currentMaterialization is not null && !currentMaterialization.IsCompleted)
                return currentMaterialization;

            currentMaterialization = materializeCoreAsync(store, ct);
            return currentMaterialization;
        }
    }

    async Task materializeCoreAsync(VectorStore store, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var symbols = await getAllSymbolsAsync(store, ct);
            var relationships = await getAllRelationshipsAsync(store, ct);

            logger.LogDebug(
                "Materializing virtual tables: {SymbolCount} symbols, {RelCount} relationships",
                symbols.Count, relationships.Count);

            var symbolById = symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);

            var relWithNames = buildRelationshipWithNames(relationships, symbolById);
            var refStats = buildSymbolReferenceStats(symbols, relationships);

            await storeRelationshipWithNamesAsync(store, relWithNames, ct);
            await storeSymbolReferenceStatsAsync(store, refStats, ct);

            logger.LogInformation(
                "Materialized virtual tables in {Elapsed}ms: {RelCount} RelationshipWithNames, {StatCount} SymbolReferenceStats",
                sw.ElapsedMilliseconds, relWithNames.Count, refStats.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to materialize virtual tables");
        }
    }

    async Task<List<SymbolRecord>> getAllSymbolsAsync(VectorStore store, CancellationToken ct)
    {
        var entry = registry.GetEntry("SymbolRecord");
        if (entry is null) return [];

        var results = new List<SymbolRecord>();
        var collection = store.GetCollection<string, SymbolRecord>("symbols");

        var asyncEnumerable = collection.GetAsync((Expression<Func<SymbolRecord, bool>>)(_ => true), int.MaxValue, cancellationToken: ct);
        await foreach (var symbol in asyncEnumerable)
        {
            if (symbol is not null)
                results.Add(symbol);
        }

        return results;
    }

    async Task<List<RelationshipRecord>> getAllRelationshipsAsync(VectorStore store, CancellationToken ct)
    {
        var entry = registry.GetEntry("RelationshipRecord");
        if (entry is null) return [];

        var results = new List<RelationshipRecord>();
        var collection = store.GetCollection<string, RelationshipRecord>("relationships");

        var asyncEnumerable = collection.GetAsync((Expression<Func<RelationshipRecord, bool>>)(_ => true), int.MaxValue, cancellationToken: ct);
        await foreach (var rel in asyncEnumerable)
        {
            if (rel is not null)
                results.Add(rel);
        }

        return results;
    }

    static List<RelationshipWithNamesRecord> buildRelationshipWithNames(
        List<RelationshipRecord> relationships,
        Dictionary<string, SymbolRecord> symbolById)
    {
        var results = new List<RelationshipWithNamesRecord>(relationships.Count);

        foreach (var rel in relationships)
        {
            symbolById.TryGetValue(rel.SourceSymbolId, out var source);
            symbolById.TryGetValue(rel.TargetSymbolId, out var target);

            results.Add(new RelationshipWithNamesRecord
            {
                Id = rel.Id,
                SourceSymbolId = rel.SourceSymbolId,
                SourceName = source?.Name ?? "",
                SourceKind = source?.Kind ?? "",
                SourceFullName = source?.FullName ?? "",
                SourceFilePath = source?.FilePath ?? "",
                TargetSymbolId = rel.TargetSymbolId,
                TargetName = target?.Name ?? "",
                TargetKind = target?.Kind ?? "",
                TargetFullName = target?.FullName ?? "",
                TargetFilePath = target?.FilePath ?? "",
                RelationshipType = rel.RelationshipType
            });
        }

        return results;
    }

    static List<SymbolReferenceStatsRecord> buildSymbolReferenceStats(
        List<SymbolRecord> symbols,
        List<RelationshipRecord> relationships)
    {
        var incomingBySymbol = new Dictionary<string, List<RelationshipRecord>>(StringComparer.Ordinal);
        var outgoingBySymbol = new Dictionary<string, List<RelationshipRecord>>(StringComparer.Ordinal);

        foreach (var rel in relationships)
        {
            if (!incomingBySymbol.ContainsKey(rel.TargetSymbolId))
                incomingBySymbol[rel.TargetSymbolId] = [];
            incomingBySymbol[rel.TargetSymbolId].Add(rel);

            if (!outgoingBySymbol.ContainsKey(rel.SourceSymbolId))
                outgoingBySymbol[rel.SourceSymbolId] = [];
            outgoingBySymbol[rel.SourceSymbolId].Add(rel);
        }

        var results = new List<SymbolReferenceStatsRecord>(symbols.Count);

        foreach (var symbol in symbols)
        {
            var incoming = incomingBySymbol.GetValueOrDefault(symbol.Id) ?? [];
            var outgoing = outgoingBySymbol.GetValueOrDefault(symbol.Id) ?? [];

            results.Add(new SymbolReferenceStatsRecord
            {
                SymbolId = symbol.Id,
                Name = symbol.Name,
                Kind = symbol.Kind,
                FullName = symbol.FullName,
                FilePath = symbol.FilePath,
                IncomingReferences = incoming.Count,
                OutgoingReferences = outgoing.Count,
                IncomingCalls = incoming.Count(r => r.RelationshipType == "Calls"),
                OutgoingCalls = outgoing.Count(r => r.RelationshipType == "Calls"),
                IncomingReferencesNonCall = incoming.Count(r => r.RelationshipType != "Calls"),
                OutgoingReferencesNonCall = outgoing.Count(r => r.RelationshipType != "Calls"),
                IncomingInherits = incoming.Count(r => r.RelationshipType == "Inherits"),
                IncomingImplements = incoming.Count(r => r.RelationshipType == "Implements"),
                IncomingTestCoverage = incoming.Count(r => r.RelationshipType == "TestCoverage"),
                OutgoingInherits = outgoing.Count(r => r.RelationshipType == "Inherits"),
                OutgoingImplements = outgoing.Count(r => r.RelationshipType == "Implements"),
                OutgoingTestCoverage = outgoing.Count(r => r.RelationshipType == "TestCoverage")
            });
        }

        return results;
    }

    async Task storeRelationshipWithNamesAsync(
        VectorStore store,
        List<RelationshipWithNamesRecord> records,
        CancellationToken ct)
    {
        var collection = store.GetCollection<string, RelationshipWithNamesRecord>("relWithNames");
        await collection.UpsertAsync(records, ct);
    }

    async Task storeSymbolReferenceStatsAsync(
        VectorStore store,
        List<SymbolReferenceStatsRecord> records,
        CancellationToken ct)
    {
        var collection = store.GetCollection<string, SymbolReferenceStatsRecord>("refStats");
        await collection.UpsertAsync(records, ct);
    }
}
