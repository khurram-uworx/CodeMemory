using System.Diagnostics.Metrics;

namespace CodeMemory.Diagnostics;

public static class CodeMemoryMetrics
{
    public static readonly Meter Meter = new("CodeMemory", "1.0");

    // Standardized tag key constants
    public static class Tags
    {
        public const string Repo = "repo";
        public const string RepoUrl = "repo.url";
        public const string Tool = "tool";
        public const string Host = "host";
    }

    // Indexing
    public static readonly Histogram<double> IndexingDuration = Meter.CreateHistogram<double>(
        "codememory.indexing.duration",
        unit: "ms",
        description: "Duration of full indexing pass per repo");

    public static readonly Histogram<long> FilesIndexed = Meter.CreateHistogram<long>(
        "codememory.indexing.files_count",
        description: "Number of files indexed per repo (per-pass)");

    public static readonly Histogram<long> SymbolsStored = Meter.CreateHistogram<long>(
        "codememory.indexing.symbols_count",
        description: "Number of symbol records stored per repo (per-pass)");

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

    // The following metrics are deliberately NOT exposed as OTel instruments
    // (dashboard-domain only — see RepoMetrics.cshtml / Metrics.cshtml):
    //
    //   Complexity.AverageLinesPerMethod       → Histogram<double>         — low signal-to-noise
    //   Complexity.MethodSizeHistogram         → 6× ObservableGauge<long>  — ad-hoc via dashboard
    //   Coupling.MostCoupled / MostImportant   → (none)                    — per-symbol topology
    //   Coupling.RelationshipTypeDistribution  → (none)                    — high cardinality
    //   TopFilesBySymbols                      → (none)                    — per-file detail
    //
    // Add instrument here + record in RepoMetricsRecorder if OTel exposure is needed later.
}
