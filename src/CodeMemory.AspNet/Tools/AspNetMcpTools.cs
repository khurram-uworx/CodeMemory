using CodeMemory.AspNet.Configuration;
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

    public AspNetMcpTools(IRepoContextAccessor repoContext)
        => this.repoContext = repoContext;

    [McpServerTool, Description("Ping the server. Returns indexing status — agents should back off and retry if still building the index.")]
    public PingResult Ping()
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "ping"), new("host", "aspnet"), new("repo.name", repoContext.CurrentRepoName ?? "unknown"));
        var repoName = repoContext.CurrentRepoName;
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
}
