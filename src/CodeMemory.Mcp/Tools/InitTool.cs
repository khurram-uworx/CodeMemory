using CodeMemory.Diagnostics;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp.Tools;

/// <summary>
/// MCP tool that initializes a repository for CodeMemory indexing by creating
/// <c>.codememory.json</c> and adding it to <c>.gitignore</c>.
/// </summary>
[McpServerToolType]
public sealed class InitTool
{
    readonly CodeMemoryInitService initService;

    /// <summary>
    /// Initializes a new instance of <see cref="InitTool"/>.
    /// </summary>
    /// <param name="initService">The service that performs initialization.</param>
    public InitTool(CodeMemoryInitService initService)
    {
        this.initService = initService;
    }

    /// <summary>
    /// Creates <c>.codememory.json</c> with all default settings and adds it to <c>.gitignore</c>.
    /// Use this as the first step when setting up a new repository for CodeMemory indexing.
    /// </summary>
    /// <param name="repoRoot">Repository root path (defaults to the currently indexed repo).</param>
    /// <param name="ct">Cancellation token.</param>
    [McpServerTool, Description("Creates .codememory.json with all default settings and adds it to .gitignore. Use this as the first step when setting up a new repository for CodeMemory indexing.")]
    public InitResult InitRepository(
        [Description("Repository root path (defaults to the currently indexed repo)")] string? repoRoot = null,
        CancellationToken ct = default)
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "init"), new("host", "mcp"));

        return initService.Run(repoRoot ?? Environment.CurrentDirectory);
    }
}
