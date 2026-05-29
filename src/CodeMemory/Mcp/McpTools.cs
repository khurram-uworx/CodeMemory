using CodeMemory.Diagnostics;
using CodeMemory.Indexing;
using CodeMemory.Mcp.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class McpTools
{
    [McpServerTool, Description("Ping the server. Returns indexing status — agents should back off and retry if still building the index.")]
    public PingResult Ping()
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "ping"), new("host", "mcp"));

        if (!IndexingState.IsCompleted())
        {
            var allProgress = IndexingState.GetAllProgress();
            var percent = allProgress.Count > 0 ? allProgress.Values.Min() : 0.0;

            return new PingResult("ok", false,
                null, null,
                percent > 0
                    ? $"Indexing in progress — {percent * 100:F0}% complete"
                    : "Indexing in progress. Retry tools in a few seconds.",
                IndexingState.Version);
        }

        return new PingResult("ok", true, IndexingState.IsFileWatcherActive,
            null, null, IndexingState.Version, IndexingState.GetRelationshipCount(null));
    }
}
