using System.Diagnostics.Metrics;

namespace CodeMemory.Diagnostics;

public static class CodeMemoryMetrics
{
    public static readonly Meter Meter = new("CodeMemory", "1.0");

    // Indexing
    public static readonly Histogram<double> IndexingDuration = Meter.CreateHistogram<double>(
        "codememory.indexing.duration",
        unit: "ms",
        description: "Duration of full indexing pass per repo");

    public static readonly Counter<long> FilesIndexed = Meter.CreateCounter<long>(
        "codememory.indexing.files_count",
        description: "Number of files indexed per repo");

    public static readonly Counter<long> SymbolsStored = Meter.CreateCounter<long>(
        "codememory.indexing.symbols_count",
        description: "Number of symbol records stored per repo");

    // Git
    public static readonly Histogram<double> CloneDuration = Meter.CreateHistogram<double>(
        "codememory.git.clone.duration",
        unit: "ms",
        description: "Duration of git clone operations");

    // Search
    public static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "codememory.search.query_duration",
        unit: "ms",
        description: "Duration of semantic search queries");

    // SQL queries
    public static readonly Histogram<double> SqlQueryDuration = Meter.CreateHistogram<double>(
        "codememory.sql.query_duration",
        unit: "ms",
        description: "Duration of custom SQL queries");

    // Tools
    public static readonly Counter<long> ToolInvocations = Meter.CreateCounter<long>(
        "codememory.tools.invocations",
        description: "Number of MCP tool invocations per repo/tool");
}
