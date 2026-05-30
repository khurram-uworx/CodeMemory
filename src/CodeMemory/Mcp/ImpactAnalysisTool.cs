using CodeMemory.Diagnostics;
using CodeMemory.Indexing.Architecture;
using CodeMemory.Indexing.Graph;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CodeMemory.Mcp;

public sealed record ImpactAnalysisResult(
    string SymbolPath,
    IReadOnlyList<DependencyNode> DownstreamDependencies,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<ComponentInfo> AffectedComponents,
    IReadOnlyList<string>? TestFiles = null,
    string? Warning = null,
    string? ContinuationToken = null);

[McpServerToolType]
public sealed class ImpactAnalysisTool
{
    readonly IDependencyGraphService? graphService;
    readonly IArchitectureService? architectureService;
    readonly ILogger<ImpactAnalysisTool> logger;

    public ImpactAnalysisTool(ILogger<ImpactAnalysisTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        graphService = serviceProvider.GetService<IDependencyGraphService>();
        architectureService = serviceProvider.GetService<IArchitectureService>();
    }

    [McpServerTool, Description("Analyzes the potential impact of changing a symbol. Returns downstream dependencies, affected files, affected components, and test coverage.")]
    public async Task<ImpactAnalysisResult> ImpactAnalysisAsync(
        [Description("Qualified symbol name to analyze")] string symbolPath,
        [Description("Maximum dependency chain depth (1-3, default 2)")] int depth = 2,
        [Description("Maximum number of dependency nodes to return (default unlimited)")] int? maxResults = null,
        [Description("Continuation token from a previous truncated response to get the next page")] string? cursor = null)
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "impact_analysis"), new("host", "mcp"));

        if (graphService == null)
        {
            logger.LogWarning("Dependency graph service not registered — returning empty impact analysis");
            return new ImpactAnalysisResult(symbolPath, [], [], [], Warning: "Dependency graph service not available");
        }

        var cappedDepth = Math.Clamp(depth, 1, 3);

        var offset = 0;
        if (cursor != null)
        {
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, int>>(
                    Convert.FromBase64String(cursor));
                if (data != null)
                    offset = data.GetValueOrDefault("o");
            }
            catch
            {
                logger.LogWarning("Invalid cursor token, ignoring");
            }
        }

        var downstreamTask = graphService.TraceAsync(symbolPath, "downstream", cappedDepth, maxResults, offset);
        var testsTask = graphService.FindTestCoverageAsync(symbolPath);

        await Task.WhenAll(downstreamTask, testsTask);

        var downstream = downstreamTask.Result;
        var testFiles = testsTask.Result;

        var affectedFiles = downstream
            .Select(n => n.FilePath)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct()
            .ToList();

        IReadOnlyList<ComponentInfo> affectedComponents = [];
        if (architectureService != null && affectedFiles.Count > 0)
        {
            try
            {
                var overview = await architectureService.GetOverviewAsync();
                var fileComponents = new HashSet<string>();
                foreach (var file in affectedFiles)
                {
                    var normalized = file.Replace('\\', '/').TrimStart('/');
                    var slashIndex = normalized.IndexOf('/');
                    var component = slashIndex > 0 ? normalized[..slashIndex] : normalized;
                    fileComponents.Add(component);
                }

                affectedComponents = overview.TopLevelComponents
                    .Where(c => fileComponents.Contains(c.Name))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve affected components");
            }
        }

        string? warning = null;
        string? continuationToken = null;
        if (maxResults.HasValue && downstream.Count >= maxResults.Value)
        {
            warning = $"Results truncated to {maxResults} nodes";
            var cursorData = new Dictionary<string, int> { ["o"] = offset + downstream.Count };
            continuationToken = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursorData));
        }

        logger.LogDebug("ImpactAnalysisAsync({Symbol}): {Downstream} downstream deps, {Files} affected files, {Tests} test files",
            symbolPath, downstream.Count, affectedFiles.Count, testFiles.Count);

        return new ImpactAnalysisResult(
            symbolPath,
            downstream,
            affectedFiles,
            affectedComponents,
            testFiles.Count > 0 ? testFiles : null,
            warning,
            continuationToken);
    }
}
