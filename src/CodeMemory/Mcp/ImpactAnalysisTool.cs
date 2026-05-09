using CodeMemory.Indexing.Architecture;
using CodeMemory.Indexing.Graph;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

public sealed record ImpactAnalysisResult(
    string SymbolPath,
    IReadOnlyList<DependencyNode> DownstreamDependencies,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<ComponentInfo> AffectedComponents,
    IReadOnlyList<string>? TestFiles = null,
    string? Warning = null);

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
        [Description("Maximum dependency chain depth (1-3, default 2)")] int depth = 2)
    {
        if (graphService == null)
        {
            logger.LogWarning("Dependency graph service not registered — returning empty impact analysis");
            return new ImpactAnalysisResult(symbolPath, [], [], [], Warning: "Dependency graph service not available");
        }

        var cappedDepth = Math.Clamp(depth, 1, 3);

        var downstreamTask = graphService.TraceAsync(symbolPath, "downstream", cappedDepth);
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

        logger.LogDebug("ImpactAnalysisAsync({Symbol}): {Downstream} downstream deps, {Files} affected files, {Tests} test files",
            symbolPath, downstream.Count, affectedFiles.Count, testFiles.Count);

        return new ImpactAnalysisResult(
            symbolPath,
            downstream,
            affectedFiles,
            affectedComponents,
            testFiles.Count > 0 ? testFiles : null);
    }
}
