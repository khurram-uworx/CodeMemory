using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Storage;
using Microsoft.EntityFrameworkCore;

namespace CodeMemory.AspNet.Services;

public sealed record RepoMetrics(
    OverviewStats Overview,
    IReadOnlyList<KindCount> SymbolKindDistribution,
    MethodComplexity Complexity,
    CouplingAnalysis Coupling,
    IReadOnlyList<FileSymbolCount> TopFilesBySymbols
);

public sealed record OverviewStats(
    int TotalSymbols,
    int TotalFiles,
    int Classes,
    int Methods,
    int Interfaces,
    int Properties,
    int Fields,
    int TotalRelationships
);

public sealed record KindCount(string Kind, int Count);

public sealed record MethodComplexity(
    double AverageLinesPerMethod,
    IReadOnlyList<MethodEntry> LongestMethods,
    IReadOnlyList<HistogramBucket> MethodSizeHistogram
);

public sealed record MethodEntry(string Name, string FullName, string FilePath, int Lines);

public sealed record HistogramBucket(string Label, int Count);

public sealed record CouplingAnalysis(
    IReadOnlyList<CoupledSymbol> MostCoupled,
    IReadOnlyList<CoupledSymbol> MostImportant,
    IReadOnlyList<RelTypeCount> RelationshipTypeDistribution
);

public sealed record CoupledSymbol(string SymbolId, string Name, string Kind, string FullName, string FilePath, int Count);

public sealed record RelTypeCount(string Type, int Count);

public sealed record FileSymbolCount(string FilePath, int Count);

public sealed class MetricsService
{
    readonly IServiceRegistry registry;

    public MetricsService(IServiceRegistry registry)
        => this.registry = registry;

    static HybridStorageService getHybridStorage(string repoName, IServiceRegistry registry)
    {
        var storage = registry.GetStorage(repoName);
        if (storage is HybridStorageService hybrid)
            return hybrid;
        throw new InvalidOperationException(
            $"Repository '{repoName}' uses in-memory storage which does not support metrics queries. " +
            "Metrics require a relational storage provider (sqlite, pgvector, or sqlserver).");
    }

    public async Task<RepoMetrics> GetMetricsAsync(string repoName)
    {
        var hybrid = getHybridStorage(repoName, registry);
        await using var db = hybrid.CreateDbContext();

        var overview = await loadOverviewAsync(db);
        var kindDist = await loadKindDistributionAsync(db);
        var complexity = await loadMethodComplexityAsync(db);
        var coupling = await loadCouplingAsync(db);
        var topFiles = await loadTopFilesAsync(db);

        return new RepoMetrics(overview, kindDist, complexity, coupling, topFiles);
    }

    static async Task<OverviewStats> loadOverviewAsync(CodeMemoryDbContext db)
    {
        var totalSymbols = await db.Symbols.CountAsync();
        var totalFiles = await db.Symbols.Select(s => s.FilePath).Distinct().CountAsync();
        var classes = await db.Symbols.CountAsync(s => s.Kind == "Class");
        var methods = await db.Symbols.CountAsync(s => s.Kind == "Method");
        var interfaces = await db.Symbols.CountAsync(s => s.Kind == "Interface");
        var properties = await db.Symbols.CountAsync(s => s.Kind == "Property");
        var fields = await db.Symbols.CountAsync(s => s.Kind == "Field");
        var totalRelationships = await db.Relationships.CountAsync();

        return new OverviewStats(totalSymbols, totalFiles, classes, methods,
            interfaces, properties, fields, totalRelationships);
    }

    static async Task<IReadOnlyList<KindCount>> loadKindDistributionAsync(CodeMemoryDbContext db)
    {
        var groups = await db.Symbols
            .GroupBy(s => s.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        return groups.Select(g => new KindCount(g.Kind, g.Count)).ToList();
    }

    static async Task<MethodComplexity> loadMethodComplexityAsync(CodeMemoryDbContext db)
    {
        var methods = await db.Symbols
            .Where(s => s.Kind == "Method")
            .Select(s => new
            {
                s.Name,
                s.FullName,
                s.FilePath,
                Lines = s.LineEnd - s.LineStart + 1
            })
            .ToListAsync();

        var lengths = methods.Select(m => m.Lines).ToList();
        var avg = lengths.Count > 0 ? lengths.Average() : 0.0;

        var longest = methods
            .OrderByDescending(m => m.Lines)
            .Take(10)
            .Select(m => new MethodEntry(m.Name, m.FullName, m.FilePath, m.Lines))
            .ToList();

        var histogram = new List<HistogramBucket>
        {
            new("1–5",    lengths.Count(l => l <= 5)),
            new("6–10",   lengths.Count(l => l >= 6 && l <= 10)),
            new("11–20",  lengths.Count(l => l >= 11 && l <= 20)),
            new("21–50",  lengths.Count(l => l >= 21 && l <= 50)),
            new("51–100", lengths.Count(l => l >= 51 && l <= 100)),
            new("100+",   lengths.Count(l => l > 100))
        };

        return new MethodComplexity(avg, longest, histogram);
    }

    static async Task<CouplingAnalysis> loadCouplingAsync(CodeMemoryDbContext db)
    {
        // Top 10 most coupled — symbols with the most outgoing relationships
        var topOutbound = await db.Relationships
            .GroupBy(r => r.SourceSymbolId)
            .Select(g => new { SymbolId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();

        var outboundIds = topOutbound.Select(x => x.SymbolId).ToHashSet();
        var outboundSymbols = outboundIds.Count > 0
            ? await db.Symbols.Where(s => outboundIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id)
            : [];

        var mostCoupled = topOutbound
            .Select(x => outboundSymbols.TryGetValue(x.SymbolId, out var s)
                ? new CoupledSymbol(s.Id, s.Name, s.Kind, s.FullName, s.FilePath, x.Count)
                : new CoupledSymbol(x.SymbolId, "?", "?", "?", "?", x.Count))
            .ToList();

        // Top 10 most important — symbols with the most incoming relationships
        var topInbound = await db.Relationships
            .GroupBy(r => r.TargetSymbolId)
            .Select(g => new { SymbolId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();

        var inboundIds = topInbound.Select(x => x.SymbolId).ToHashSet();
        var inboundSymbols = inboundIds.Count > 0
            ? await db.Symbols.Where(s => inboundIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id)
            : [];

        var mostImportant = topInbound
            .Select(x => inboundSymbols.TryGetValue(x.SymbolId, out var s)
                ? new CoupledSymbol(s.Id, s.Name, s.Kind, s.FullName, s.FilePath, x.Count)
                : new CoupledSymbol(x.SymbolId, "?", "?", "?", "?", x.Count))
            .ToList();

        // Relationship type distribution
        var relGroups = await db.Relationships
            .GroupBy(r => r.RelationshipType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();
        var relTypeDist = relGroups.Select(g => new RelTypeCount(g.Type, g.Count)).ToList();

        return new CouplingAnalysis(mostCoupled, mostImportant, relTypeDist);
    }

    static async Task<IReadOnlyList<FileSymbolCount>> loadTopFilesAsync(CodeMemoryDbContext db)
    {
        var groups = await db.Symbols
            .GroupBy(s => s.FilePath)
            .Select(g => new { FilePath = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();

        return groups.Select(g => new FileSymbolCount(g.FilePath, g.Count)).ToList();
    }
}
