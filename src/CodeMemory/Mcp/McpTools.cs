using CodeMemory.Indexing;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class McpTools
{
    [McpServerTool, Description("Ping the server. Returns indexing status — agents should back off and retry if still building the index.")]
    public string Ping()
    {
        if (IndexingState.IsCompleted())
            return """{"status":"ok","indexingCompleted":true}""";

        return """{"status":"ok","indexingCompleted":false,"message":"Indexing in progress. Retry tools in a few seconds."}""";
    }
}
