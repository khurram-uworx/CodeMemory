using CodeMemory.Diagnostics;
using CodeMemory.Indexing.Graph;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

public sealed record FindRelatedCodeResult(
    IReadOnlyList<DependencyNode> Results,
    DependencyNode? MatchedSymbol = null,
    string? Message = null,
    IReadOnlyList<string>? Suggestions = null);

[McpServerToolType]
public sealed class FindRelatedCodeTool
{
    readonly IDependencyGraphService? graphService;
    readonly IStorageService? storage;
    readonly ILogger<FindRelatedCodeTool> logger;

    public FindRelatedCodeTool(ILogger<FindRelatedCodeTool> logger,
        IServiceProvider serviceProvider)
    {
        this.logger = logger;
        graphService = serviceProvider.GetService<IDependencyGraphService>();
        storage = serviceProvider.GetService<IStorageService>();
    }

    [McpServerTool, Description("Finds code related to a given symbol by traversing dependency relationships. Returns related symbols grouped by relation type. When the symbol is not found or has no relationships of the requested type, returns a diagnostic message and suggestions instead of silently returning empty.")]
    public async Task<FindRelatedCodeResult> FindRelatedCodeAsync(
        [Description("Qualified symbol name to find related code for")] string symbolPath,
        [Description("Filter by relation type: 'all', 'calls', 'references', 'inherits', 'implements'")] string relationType = "all")
    {
        CodeMemoryMetrics.ToolInvocations.Add(1, new("tool", "find_related_code"), new("host", "mcp"));

        if (graphService == null)
        {
            logger.LogWarning("Dependency graph service not registered — returning empty result");
            return new FindRelatedCodeResult([], Message: "Dependency graph service not registered.");
        }

        IReadOnlyList<DependencyNode> related;
        try
        {
            related = await graphService.FindRelatedAsync(symbolPath, relationType);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FindRelatedCodeAsync({Symbol}): dependency graph service failed to resolve", symbolPath);
            return new FindRelatedCodeResult([], Message: $"Symbol '{symbolPath}' could not be resolved by the dependency graph service.");
        }

        if (related.Count > 0)
        {
            logger.LogDebug("FindRelatedCodeAsync({Symbol}, {Type}): {Count} results",
                symbolPath, relationType, related.Count);
            return new FindRelatedCodeResult(related);
        }

        var diagnostic = await BuildDiagnosticAsync(symbolPath, relationType);
        logger.LogWarning("FindRelatedCodeAsync({Symbol}, {Type}): no results — {Message}",
            symbolPath, relationType, diagnostic.Message);

        return new FindRelatedCodeResult([], diagnostic.MatchedSymbol, diagnostic.Message, diagnostic.Suggestions);
    }

    async Task<(DependencyNode? MatchedSymbol, string? Message, IReadOnlyList<string>? Suggestions)>
        BuildDiagnosticAsync(string symbolPath, string relationType)
    {
        try
        {
            var symbol = await SymbolLookup.ResolveAsync(storage, symbolPath);
            if (symbol == null)
            {
                var suggestions = await SymbolLookup.SuggestAsync(storage, symbolPath);
                var message = suggestions.Count > 0
                    ? $"Symbol '{symbolPath}' not found in index. Did you mean one of: {string.Join("; ", suggestions)}?"
                    : $"Symbol '{symbolPath}' not found in index. Check the spelling or query sql_query for available symbols.";
                return (null, message, suggestions);
            }

            if (relationType is "all")
                return (SymbolLookup.ToNode(symbol),
                    $"Symbol '{symbolPath}' resolved to '{symbol.FullName}', but no relationships are indexed for it.",
                    null);

            var available = await SymbolLookup.AvailableRelationTypesAsync(storage, symbol.Id);
            var typeMessage = available is "(none)"
                ? $"Symbol '{symbolPath}' resolved to '{symbol.FullName}', but no relationships are indexed for it."
                : $"No '{relationType}' relationships found for '{symbolPath}'. Available relationship types involving this symbol: {available}. Try relationType=\"all\".";
            return (SymbolLookup.ToNode(symbol), typeMessage, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build find_related_code diagnostics for {Symbol}", symbolPath);
            return (null, $"Symbol '{symbolPath}' could not be resolved.", null);
        }
    }
}