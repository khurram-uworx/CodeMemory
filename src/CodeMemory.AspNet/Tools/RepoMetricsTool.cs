using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Services;
using CodeMemory.Diagnostics;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.AspNet.Tools;

public sealed record RepoMetricsResult(
    string Status,
    RepoMetricsSnapshot? Snapshot,
    string? Message
);

[McpServerToolType]
public sealed class RepoMetricsTool
{
    readonly LocalMetricsCollector? collector;

    public RepoMetricsTool(IServiceProvider sp)
        => collector = sp.GetService<LocalMetricsCollector>();

    [McpServerTool, Description("Returns repo metrics collected by the local in-process metrics collector. Requires LocalMetrics:Enabled=true in configuration.")]
    public RepoMetricsResult GetRepoMetrics()
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "get_repo_metrics"), new("host", "aspnet"));

        if (collector is null || !collector.IsEnabled)
        {
            return new RepoMetricsResult(
                "not_available",
                null,
                "Local metrics are not enabled. Set LocalMetrics:Enabled=true in appsettings.json.");
        }

        var snapshot = collector.GetSnapshot();
        return new RepoMetricsResult("ok", snapshot, null);
    }
}
