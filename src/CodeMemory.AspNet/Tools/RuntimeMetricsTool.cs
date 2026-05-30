using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Services;
using CodeMemory.Diagnostics;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeMemory.AspNet.Tools;

[McpServerToolType]
public sealed class RuntimeMetricsTool
{
    readonly LocalMetricsCollector? collector;

    public RuntimeMetricsTool(IServiceProvider sp)
        => collector = sp.GetService<LocalMetricsCollector>();

    [McpServerTool, Description("Returns runtime metrics collected by the local in-process metrics collector. Requires LocalMetrics:Enabled=true in configuration.")]
    public string GetRuntimeMetrics()
    {
        try
        {
            if (collector is null || !collector.IsEnabled)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "not_available",
                    snapshot = (object?)null,
                    message = "Local metrics are not enabled. Set LocalMetrics:Enabled=true in appsettings.json."
                }, SerializerOptions);
            }

            var snapshot = collector.GetSnapshot();
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot,
                message = (string?)null
            }, SerializerOptions);
        }
        catch (Exception ex)
        {
            CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "get_runtime_metrics"), new("host", "aspnet"));
            return JsonSerializer.Serialize(new
            {
                status = "error",
                snapshot = (object?)null,
                message = ex.Message
            }, SerializerOptions);
        }
    }

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
