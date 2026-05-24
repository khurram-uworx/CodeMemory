using CodeMemory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Linq.Expressions;

namespace CodeMemory.Mcp.Tools;

[McpServerToolType]
public sealed class MostReferencedSymbolsTool
{
    readonly IStorageService? storageService;
    readonly ILogger<MostReferencedSymbolsTool> logger;

    public MostReferencedSymbolsTool(
        ILogger<MostReferencedSymbolsTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        storageService = serviceProvider.GetService<IStorageService>();
    }

    [McpServerTool, Description(@"
Finds the most referenced symbols in the repository.

Returns symbols ranked by their reference count (incoming by default).
No SQL or JOINs required - this tool handles all the complexity internally.

Use this to answer:
- What is the most famous/used class?
- Which methods are called most often?
- What interfaces are most implemented?
- What's the coupling surface of a component?

Filter by kind to get specific symbol types:
- 'Class', 'Interface', 'Struct', 'Enum', 'Record'
- 'Method', 'Property', 'Field', 'Event'

Filter by relationType for specific relationship types:
- 'References' (general reference, default)
- 'Calls' (method calls only)
- 'Inherits' (class inheritance)
- 'Implements' (interface implementation)
- 'TestCoverage' (test references to code)

Direction:
- 'incoming' (default): what symbols reference THIS one (popularity)
- 'outgoing': what symbols THIS one references (dependencies)
")]
    public async Task<MostReferencedResult> GetMostReferencedAsync(
        [Description("Optional: Filter by symbol kind (Class, Method, Interface, etc.)")]
        string? kind = null,
        [Description("Optional: Filter by relationship type (References, Calls, Inherits, Implements, TestCoverage)")]
        string? relationType = null,
        [Description("Direction: 'incoming' (default) for what references this symbol, 'outgoing' for what this symbol references")]
        string direction = "incoming",
        [Description("Maximum number of results to return (default 10)")]
        int limit = 10)
    {
        if (storageService?.Store is null)
        {
            logger.LogWarning("MostReferencedSymbols called without storage available");
            return new MostReferencedResult([], "Storage not available");
        }

        var store = storageService.Store;
        var collection = store.GetCollection<string, SqlQuery.SymbolReferenceStatsRecord>("refStats");

        var stats = new List<SqlQuery.SymbolReferenceStatsRecord>();
        var asyncEnumerable = collection.GetAsync((Expression<Func<SqlQuery.SymbolReferenceStatsRecord, bool>>)(_ => true), int.MaxValue, cancellationToken: default);
        await foreach (var item in asyncEnumerable)
        {
            if (item is not null)
                stats.Add(item);
        }

        if (stats.Count == 0)
        {
            return new MostReferencedResult([], "No reference statistics available. Indexing may still be in progress.");
        }

        var filtered = stats.AsEnumerable();

        if (!string.IsNullOrEmpty(kind))
            filtered = filtered.Where(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));

        var results = filtered.Select(s => new MostReferencedSymbol(
            SymbolName: s.Name,
            Kind: s.Kind,
            FullName: s.FullName,
            FilePath: s.FilePath,
            ReferenceCount: GetCount(s, direction, relationType),
            Breakdown: GetBreakdown(s, direction)
        )).ToList();

        results.Sort((a, b) => b.ReferenceCount.CompareTo(a.ReferenceCount));

        var cappedLimit = Math.Clamp(limit, 1, 100);
        var finalResults = results.Take(cappedLimit).ToList();

        return new MostReferencedResult(finalResults, null);
    }

    static long GetCount(SqlQuery.SymbolReferenceStatsRecord s, string direction, string? relationType)
    {
        var isOutgoing = string.Equals(direction, "outgoing", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(relationType))
            return isOutgoing ? s.OutgoingReferences : s.IncomingReferences;

        return relationType.ToUpperInvariant() switch
        {
            "CALLS" => isOutgoing ? s.OutgoingCalls : s.IncomingCalls,
            "INHERITS" => isOutgoing ? s.OutgoingInherits : s.IncomingInherits,
            "IMPLEMENTS" => isOutgoing ? s.OutgoingImplements : s.IncomingImplements,
            "TESTCOVERAGE" => isOutgoing ? s.OutgoingTestCoverage : s.IncomingTestCoverage,
            _ => isOutgoing
                ? s.OutgoingReferencesNonCall + s.OutgoingCalls
                : s.IncomingReferencesNonCall + s.IncomingCalls
        };
    }

    static ReferenceBreakdown GetBreakdown(SqlQuery.SymbolReferenceStatsRecord s, string direction)
    {
        var isOutgoing = string.Equals(direction, "outgoing", StringComparison.OrdinalIgnoreCase);

        return new ReferenceBreakdown(
            Calls: isOutgoing ? s.OutgoingCalls : s.IncomingCalls,
            References: isOutgoing ? s.OutgoingReferencesNonCall : s.IncomingReferencesNonCall,
            Inherits: isOutgoing ? s.OutgoingInherits : s.IncomingInherits,
            Implements: isOutgoing ? s.OutgoingImplements : s.IncomingImplements,
            TestCoverage: isOutgoing ? s.OutgoingTestCoverage : s.IncomingTestCoverage
        );
    }
}

public sealed record MostReferencedResult(
    IReadOnlyList<MostReferencedSymbol> Symbols,
    string? Warning = null);

public sealed record MostReferencedSymbol(
    string SymbolName,
    string Kind,
    string FullName,
    string FilePath,
    long ReferenceCount,
    ReferenceBreakdown Breakdown);

public sealed record ReferenceBreakdown(
    long Calls,
    long References,
    long Inherits,
    long Implements,
    long TestCoverage);
