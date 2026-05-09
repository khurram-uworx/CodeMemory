using CodeMemory.Indexing.Graph;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class TraceDependencyTool
{
    readonly IDependencyGraphService? graphService;
    readonly ILogger<TraceDependencyTool> logger;

    public TraceDependencyTool(ILogger<TraceDependencyTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        graphService = serviceProvider.GetService<IDependencyGraphService>();
    }

    [McpServerTool, Description("Traces dependency chains for a given symbol. Finds related symbols, call graphs, and optionally test coverage.")]
    public async Task<DependencyResult> TraceDependencyAsync(
        [Description("Qualified symbol name to trace")] string symbolPath,
        [Description("Direction: 'upstream' (what the symbol depends on), 'downstream' (what depends on it), or 'both'")] string direction = "downstream",
        [Description("Filter by relation type: 'all', 'calls', 'imports', 'references', 'inheritance'")] string relationType = "all",
        [Description("Maximum chain depth (1-3, default 1)")] int depth = 1,
        [Description("Whether to include test coverage as a relation")] bool includeTests = false)
    {
        if (graphService == null)
        {
            logger.LogWarning("Dependency graph service not registered — returning empty result");
            return new DependencyResult([], []);
        }

        var cappedDepth = Math.Clamp(depth, 1, 3);

        var traceTask = graphService.TraceAsync(symbolPath, direction, cappedDepth);
        var relatedTask = graphService.FindRelatedAsync(symbolPath, relationType);
        var testsTask = includeTests
            ? graphService.FindTestCoverageAsync(symbolPath)
            : Task.FromResult<IReadOnlyList<string>>([]);

        await Task.WhenAll(traceTask, relatedTask, testsTask);

        var testFiles = testsTask.Result
            .Select(t => new DependencyNode(t, "", "TestFile", "", "TestCoverage"))
            .ToList();

        return new DependencyResult(
            DependencyChain: traceTask.Result,
            RelatedSymbols: relatedTask.Result,
            TestFiles: testFiles.Count > 0 ? testFiles : null
        );
    }
}

public sealed record DependencyResult(
    IReadOnlyList<DependencyNode> DependencyChain,
    IReadOnlyList<DependencyNode> RelatedSymbols,
    IReadOnlyList<DependencyNode>? TestFiles = null);
