using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Services;
using CodeMemory.Diagnostics;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.AspNet.Tools;

public sealed record RuntimeMetricsResult(
    string Status,
    RuntimeMetricsSnapshot? Snapshot,
    string? Message
);

[McpServerToolType]
public sealed class RuntimeMetricsTool
{
    readonly LocalMetricsCollector? collector;

    public RuntimeMetricsTool(IServiceProvider sp)
        => collector = sp.GetService<LocalMetricsCollector>();

    [McpServerTool, Description("Returns runtime metrics collected by the local in-process metrics collector. Requires LocalMetrics:Enabled=true in configuration.")]
    public RuntimeMetricsResult GetRuntimeMetrics()
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "get_runtime_metrics"), new("host", "aspnet"));

        if (collector is null || !collector.IsEnabled)
        {
            return new RuntimeMetricsResult(
                "not_available",
                null,
                "Local metrics are not enabled. Set LocalMetrics:Enabled=true in appsettings.json.");
        }

        var snapshot = collector.GetSnapshot();
        return new RuntimeMetricsResult("ok", snapshot, null);
    }
}
