using CodeMemory.AspNet.Configuration;
using CodeMemory.Diagnostics;
using CodeMemory.Indexing;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CodeMemory.AspNet.Tools;

[McpServerToolType]
public sealed class AspNetMcpTools
{
    readonly IRepoContextAccessor repoContext;

    public AspNetMcpTools(IRepoContextAccessor repoContext)
        => this.repoContext = repoContext;

    [McpServerTool, Description("Ping the server. Returns indexing status — agents should back off and retry if still building the index.")]
    public string Ping()
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "ping"), new("host", "aspnet"));
        var repoName = repoContext.CurrentRepoName;
        if (repoName is null)
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                indexingCompleted = false,
                message = "No repo context available."
            });

        if (!IndexingState.IsCompleted(repoName))
        {
            var percent = IndexingState.GetProgress(repoName);
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                indexingCompleted = false,
                repo = repoName,
                message = percent is > 0
                    ? $"Indexing in progress — {percent * 100:F0}% complete"
                    : "Indexing in progress. Retry tools in a few seconds."
            });
        }

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            indexingCompleted = true,
            repo = repoName
        });
    }
}
