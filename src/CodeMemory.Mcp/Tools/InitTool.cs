using CodeMemory.Diagnostics;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp.Tools;

[McpServerToolType]
public sealed class InitTool
{
    readonly CodeMemoryInitService initService;

    public InitTool(CodeMemoryInitService initService)
    {
        this.initService = initService;
    }

    [McpServerTool, Description("Creates .codememory.json with all default settings and adds it to .gitignore. Use this as the first step when setting up a new repository for CodeMemory indexing.")]
    public InitResult InitRepository(
        [Description("Repository root path (defaults to the currently indexed repo)")] string? repoRoot = null,
        CancellationToken ct = default)
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "init"), new("host", "mcp"));

        return initService.Run(repoRoot ?? Environment.CurrentDirectory);
    }
}
