using CodeMemory.Indexing.Graph;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

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
        [Description("Whether to include test coverage as a relation")] bool includeTests = false,
        [Description("Maximum number of dependency nodes to return (default unlimited)")] int? maxResults = null,
        [Description("Continuation token from a previous truncated response to get the next page")] string? cursor = null)
    {
        if (graphService == null)
        {
            logger.LogWarning("Dependency graph service not registered — returning empty result");
            return new DependencyResult([], []);
        }

        var cappedDepth = Math.Clamp(depth, 1, 3);

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

        var traceTask = graphService.TraceAsync(symbolPath, direction, cappedDepth, maxResults, depOffset);
        var relatedTask = graphService.FindRelatedAsync(symbolPath, relationType, maxResults, relOffset);
        var testsTask = includeTests
            ? graphService.FindTestCoverageAsync(symbolPath)
            : Task.FromResult<IReadOnlyList<string>>([]);

        await Task.WhenAll(traceTask, relatedTask, testsTask);

        var traceResult = traceTask.Result;
        var relatedResult = relatedTask.Result;

        var testFiles = testsTask.Result
            .Select(t => new DependencyNode(t, "", "TestFile", "", "TestCoverage"))
            .ToList();

        string? warning = null;
        string? continuationToken = null;
        if (maxResults.HasValue && traceResult.Count >= maxResults.Value)
        {
            warning = $"Dependency chain truncated to {maxResults} nodes";
            var cursorData = new Dictionary<string, int>
            {
                ["do"] = depOffset + traceResult.Count,
                ["ro"] = relOffset + relatedResult.Count
            };
            continuationToken = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursorData));
        }

        return new DependencyResult(
            DependencyChain: traceResult,
            RelatedSymbols: relatedResult,
            TestFiles: testFiles.Count > 0 ? testFiles : null,
            Warning: warning,
            ContinuationToken: continuationToken
        );
    }
}

public sealed record DependencyResult(
    IReadOnlyList<DependencyNode> DependencyChain,
    IReadOnlyList<DependencyNode> RelatedSymbols,
    IReadOnlyList<DependencyNode>? TestFiles = null,
    string? Warning = null,
    string? ContinuationToken = null);
