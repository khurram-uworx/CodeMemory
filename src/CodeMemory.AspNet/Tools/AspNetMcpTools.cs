using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Services;
using CodeMemory.Diagnostics;
using CodeMemory.Indexing;
using CodeMemory.Mcp.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.AspNet.Tools;

[McpServerToolType]
public sealed class AspNetMcpTools
{
    readonly IRepoContextAccessor repoContext;
    readonly ILocalMetricsCollector localMetrics;

    public AspNetMcpTools(IRepoContextAccessor repoContext, ILocalMetricsCollector localMetrics)
        => (this.repoContext, this.localMetrics) = (repoContext, localMetrics);

    [McpServerTool, Description("Ping the server. Returns indexing status — agents should back off and retry if still building the index.")]
    public PingResult Ping()
    {
        var repoName = repoContext.CurrentRepoName;
        using var metricsScope = CodeMemoryMetrics.BeginRepoScope(repoName);
        CodeMemoryMetrics.AddToolInvocation("ping", "aspnet");

        if (repoName is null)
            return new PingResult("ok", false, null, null, "No repo context available.", IndexingState.Version);

        if (!IndexingState.IsCompleted(repoName))
        {
            var percent = IndexingState.GetProgress(repoName);
            return new PingResult("ok", false,
                null, repoName,
                percent is > 0
                    ? $"Indexing in progress — {percent * 100:F0}% complete"
                    : "Indexing in progress. Retry tools in a few seconds.",
                IndexingState.Version);
        }

        return new PingResult("ok", true, null, repoName,
            null, IndexingState.Version, IndexingState.GetRelationshipCount(repoName));
    }

    [McpServerTool, Description("Return the local in-process runtime metrics snapshot for the current repository. Requires Observability:LocalMetrics:Enabled.")]
    public LocalMetricsSnapshot GetMetricsSnapshot(
        [Description("Optional metric name filter, such as codememory.tools.invocations.")]
        string? metricName = null,
        [Description("Whether to include individual metric series. Set false for summary-only responses.")]
        bool includeSeries = true)
    {
        var repoName = repoContext.CurrentRepoName;
        using var metricsScope = CodeMemoryMetrics.BeginRepoScope(repoName);
        CodeMemoryMetrics.AddToolInvocation("get_metrics_snapshot", "aspnet");

        return localMetrics.GetSnapshot(repoName, metricName, includeSeries);
    }
}
