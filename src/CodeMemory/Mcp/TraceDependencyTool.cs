using CodeMemory.Diagnostics;
using CodeMemory.Indexing.Graph;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CodeMemory.Mcp;

[McpServerToolType]
public sealed class TraceDependencyTool
{
    readonly IDependencyGraphService? graphService;
    readonly IStorageService? storage;
    readonly ILogger<TraceDependencyTool> logger;

    public TraceDependencyTool(ILogger<TraceDependencyTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        graphService = serviceProvider.GetService<IDependencyGraphService>();
        storage = serviceProvider.GetService<IStorageService>();
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
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "trace_dependency"), new("host", "mcp"));

        if (graphService == null)
        {
            logger.LogWarning("Dependency graph service not registered — returning empty result");
            return new DependencyResult([], [], Warning: "Dependency graph service not registered.");
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

        if (cursor == null && traceResult.Count == 0 && relatedResult.Count == 0 && testFiles.Count == 0)
            return await BuildEmptyDiagnosticAsync(symbolPath);

        return new DependencyResult(
            DependencyChain: traceResult,
            RelatedSymbols: relatedResult,
            TestFiles: testFiles.Count > 0 ? testFiles : null,
            Warning: warning,
            ContinuationToken: continuationToken
        );
    }

    /// <summary>
    /// Replaces empty results with an actionable diagnostic when the symbol cannot be resolved.
    /// </summary>
    async Task<DependencyResult> BuildEmptyDiagnosticAsync(string symbolPath)
    {
        try
        {
            var symbol = await SymbolLookup.ResolveAsync(storage, symbolPath);
            if (symbol != null)
            {
                logger.LogDebug("TraceDependencyAsync({Symbol}): symbol found but no relationships indexed", symbolPath);
                return new DependencyResult([], [],
                    MatchedSymbol: SymbolLookup.ToNode(symbol),
                    Warning: $"Symbol '{symbolPath}' resolved to '{symbol.FullName}', but no dependency relationships are indexed for it.");
            }

            var suggestions = await SymbolLookup.SuggestAsync(storage, symbolPath);
            var message = suggestions.Count > 0
                ? $"Symbol '{symbolPath}' not found in index. Did you mean one of: {string.Join("; ", suggestions)}?"
                : $"Symbol '{symbolPath}' not found in index. Check the spelling or query sql_query for available symbols.";
            logger.LogWarning("TraceDependencyAsync({Symbol}): symbol not found — {Message}", symbolPath, message);
            return new DependencyResult([], [], Warning: message, Suggestions: suggestions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build trace_dependency diagnostics for {Symbol}", symbolPath);
            return new DependencyResult([], [], Warning: $"Symbol '{symbolPath}' could not be resolved.");
        }
    }
}

public sealed record DependencyResult(
    IReadOnlyList<DependencyNode> DependencyChain,
    IReadOnlyList<DependencyNode> RelatedSymbols,
    IReadOnlyList<DependencyNode>? TestFiles = null,
    string? Warning = null,
    string? ContinuationToken = null,
    DependencyNode? MatchedSymbol = null,
    IReadOnlyList<string>? Suggestions = null);
