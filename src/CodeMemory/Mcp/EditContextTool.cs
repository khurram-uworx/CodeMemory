using CodeMemory.Mcp.Models;
using CodeMemory.Mcp.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

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
        [Description("Include source code text")] bool includeSourceCode = true)
    {
        if (editContextService == null)
        {
            logger.LogWarning("Edit context service not registered — returning minimal context");
            return new EditContext(
                new TargetInfo(symbolPath, "", "", ""),
                null, null, null, null,
                DateTimeOffset.UtcNow,
                ["Edit context service not available"]);
        }

        var options = new EditContextOptions(includeDependencies, Math.Clamp(depth, 1, 3), includeSourceCode);
        return await editContextService.GetEditContextAsync(symbolPath, options);
    }
}
