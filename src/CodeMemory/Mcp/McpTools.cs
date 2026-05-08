using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class McpTools
{
    [McpServerTool, Description("Simple ping to verify the MCP server is responding")]
    public string Ping()
    {
        return """{"status":"ok"}""";
    }
}
