using CodeMemory.Indexing.Architecture;
using CodeMemory.Indexing.Git;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CodeMemory.Mcp;

[McpServerResourceType]
public sealed class CodeMemoryResources
{
    readonly IArchitectureService? architectureService;
    readonly IGitHistoryService? gitHistoryService;
    readonly ILogger<CodeMemoryResources> logger;

    public CodeMemoryResources(ILogger<CodeMemoryResources> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        architectureService = serviceProvider.GetService<IArchitectureService>();
        gitHistoryService = serviceProvider.GetService<IGitHistoryService>();
    }

    [McpServerResource(UriTemplate = "codememory://architecture/overview", Name = "Architecture Overview", MimeType = "application/json")]
    [Description("Returns a high-level overview of the repository structure: top-level components, language breakdown, file counts, and symbol counts.")]
    public async Task<string> GetArchitectureOverviewAsync(CancellationToken ct = default)
    {
        if (architectureService == null)
        {
            logger.LogWarning("Architecture service not registered");
            return JsonSerializer.Serialize(new { status = "error", message = "Architecture service not available" });
        }

        var overview = await architectureService.GetOverviewAsync(null, 1, ct);
        return JsonSerializer.Serialize(overview);
    }

    [McpServerResource(UriTemplate = "codememory://hotspots", Name = "Hotspots", MimeType = "application/json")]
    [Description("Returns the most frequently changed files in the repository (hotspots). Files are ranked by commit count.")]
    public async Task<string> GetHotspotsAsync(CancellationToken ct = default)
    {
        if (gitHistoryService == null)
        {
            logger.LogWarning("GitHistoryService not registered");
            return JsonSerializer.Serialize(new { status = "error", message = "Git history service not available" });
        }

        var hotspots = await gitHistoryService.GetHotspotsAsync(10, 25, ct);
        return JsonSerializer.Serialize(hotspots);
    }
}
