using CodeMemory.AspNet.Configuration;
using CodeMemory.Indexing;
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
    public string Ping()
    {
        var repoName = repoContext.CurrentRepoName;
        if (repoName is null)
            return """{"status":"ok","indexingCompleted":false,"message":"No repo context available."}""";

        if (!IndexingState.IsCompleted(repoName))
            return $$"""{"status":"ok","indexingCompleted":false,"repo":"{{repoName}}","message":"Indexing in progress. Retry tools in a few seconds."}""";

        return $$"""{"status":"ok","indexingCompleted":true,"repo":"{{repoName}}","host":"aspnet","transport":"streamable-http"}""";
    }
}
