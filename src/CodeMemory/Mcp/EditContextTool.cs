using CodeMemory.Diagnostics;
using CodeMemory.Mcp.Models;
using CodeMemory.Mcp.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class EditContextTool
{
    readonly IEditContextService? editContextService;
    readonly ILogger<EditContextTool> logger;

    public EditContextTool(ILogger<EditContextTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        editContextService = serviceProvider.GetService<IEditContextService>();
    }

    [McpServerTool, Description("Returns comprehensive edit context for a symbol: target info, source code, dependency chains, related symbols, and test coverage.")]
    public async Task<EditContext> GetEditContextAsync(
        [Description("Qualified symbol name to get context for")] string symbolPath,
        [Description("Include dependency and test information")] bool includeDependencies = true,
        [Description("Maximum dependency chain depth (1-3)")] int depth = 1,
        [Description("Include source code text")] bool includeSourceCode = true,
        [Description("Maximum number of dependency nodes to return (default unlimited)")] int? maxResults = null,
        [Description("Continuation token from a previous truncated response to get the next page")] string? cursor = null)
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "get_edit_context"), new("host", "mcp"));

        if (editContextService == null)
        {
            logger.LogWarning("Edit context service not registered — returning minimal context");
            return new EditContext(
                new TargetInfo(symbolPath, "", "", ""),
                null, null, null, null,
                DateTimeOffset.UtcNow,
                ["Edit context service not available"]);
        }

        var depOffset = 0;
        var relOffset = 0;
        if (cursor != null)
        {
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, int>>(
                    Convert.FromBase64String(cursor));
                if (data != null)
                {
                    depOffset = data.GetValueOrDefault("do");
                    relOffset = data.GetValueOrDefault("ro");
                }
            }
            catch
            {
                logger.LogWarning("Invalid cursor token, ignoring");
            }
        }

        var options = new EditContextOptions(includeDependencies, Math.Clamp(depth, 1, 3), includeSourceCode, maxResults, depOffset, relOffset);
        return await editContextService.GetEditContextAsync(symbolPath, options);
    }
}
