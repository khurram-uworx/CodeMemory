using CodeMemory.Indexing;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class McpTools
{
    [McpServerTool, Description("Ping the server. Returns indexing status — agents should back off and retry if still building the index.")]
    public string Ping()
    {
        if (!IndexingState.IsCompleted())
        {
            var allProgress = IndexingState.GetAllProgress();
            var percent = allProgress.Count > 0 ? allProgress.Values.Min() : 0.0;

            return JsonSerializer.Serialize(new
            {
                status = "ok",
                indexingCompleted = false,
                message = percent > 0
                    ? $"Indexing in progress — {percent * 100:F0}% complete"
                    : "Indexing in progress. Retry tools in a few seconds."
            });
        }

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            indexingCompleted = true,
            fileWatcherActive = IndexingState.IsFileWatcherActive
        });
    }
}
