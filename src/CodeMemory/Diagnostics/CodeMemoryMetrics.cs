using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace CodeMemory.Diagnostics;

public static class CodeMemoryMetrics
{
    public static readonly Meter Meter = new("CodeMemory", "1.0");
    static readonly AsyncLocal<string?> CurrentRepoName = new();

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

    public static IDisposable BeginRepoScope(string? repoName)
    {
        var prior = CurrentRepoName.Value;
        CurrentRepoName.Value = string.IsNullOrWhiteSpace(repoName) ? null : repoName;
        return new RepoScope(prior);
    }

    public static void RecordIndexingDuration(double milliseconds)
        => IndexingDuration.Record(milliseconds, currentRepoTags());

    public static void AddFilesIndexed(long count)
        => FilesIndexed.Add(count, currentRepoTags());

    public static void AddSymbolsStored(long count)
        => SymbolsStored.Add(count, currentRepoTags());

    public static void RecordSearchQueryDuration(double milliseconds)
        => QueryDuration.Record(milliseconds, currentRepoTags());

    public static void RecordSqlQueryDuration(double milliseconds)
        => SqlQueryDuration.Record(milliseconds, currentRepoTags());

    public static void AddToolInvocation(string tool, string host)
    {
        var tags = CurrentRepoName.Value is { Length: > 0 } repoName
            ? new TagList { { "tool", tool }, { "host", host }, { "repo.name", repoName } }
            : new TagList { { "tool", tool }, { "host", host } };

        ToolInvocations.Add(1, tags);
    }

    static TagList currentRepoTags()
        => CurrentRepoName.Value is { Length: > 0 } repoName
            ? new TagList { { "repo.name", repoName } }
            : new TagList();

    sealed class RepoScope : IDisposable
    {
        readonly string? prior;

        public RepoScope(string? prior)
            => this.prior = prior;

        public void Dispose()
            => CurrentRepoName.Value = prior;
    }
}
