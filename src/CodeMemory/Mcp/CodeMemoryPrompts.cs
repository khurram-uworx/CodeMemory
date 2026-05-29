using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp;

[McpServerPromptType]
public static class CodeMemoryPrompts
{
    [McpServerPrompt(Name = "architecture-overview", Title = "Architecture Overview")]
    [Description("Get a high-level overview of the repository structure, including top-level components, language breakdown, file counts, and symbol counts.")]
    public static GetPromptResult GetArchitectureOverview(
        [Description("Optional subdirectory path to focus the overview on")] string? path = null)
    {
        var text = path is not null
            ? $"Explore and summarize the codebase architecture, focusing on the '{path}' directory. Use get_architecture_overview to fetch the overview, and optionally get_component_clusters for component relationship analysis."
            : "Explore and summarize the codebase architecture. Use get_architecture_overview to fetch the overview, and optionally get_component_clusters for component relationship analysis.";

        return new GetPromptResult
        {
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock { Text = text }
                }
            ],
            Description = "Repository architecture analysis prompt"
        };
    }

    [McpServerPrompt(Name = "analyze-symbol", Title = "Analyze Symbol")]
    [Description("Deeply analyze a specific code symbol — its definition, dependencies, usage patterns, and recent change history.")]
    public static GetPromptResult AnalyzeSymbol(
        [Description("Fully qualified symbol name (e.g., MyClass.MyMethod)")] string symbolPath)
    {
        return new GetPromptResult
        {
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text =
                            $"Analyze the symbol '{symbolPath}' in detail.\n\n" +
                            "Use get_edit_context to fetch structured context including source code, dependencies, and test coverage.\n" +
                            "Use trace_dependency to understand upstream (what it depends on) and downstream (what depends on it) dependents.\n" +
                            "Use get_symbol_history to see recent commits affecting this symbol."
                    }
                }
            ],
            Description = $"In-depth analysis of {symbolPath}"
        };
    }

    [McpServerPrompt(Name = "impact-review", Title = "Impact Review")]
    [Description("Analyze the potential downstream impact of changing a specific symbol.")]
    public static GetPromptResult ImpactReview(
        [Description("Fully qualified symbol name to analyze impact for")] string symbolPath,
        [Description("Maximum dependency chain traversal depth (1-3, default 2)")] string? depth = "2")
    {
        if (!int.TryParse(depth, out int parsedDepth))
            parsedDepth = 2;

        return new GetPromptResult
        {
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text =
                            $"Analyze the downstream impact of changing '{symbolPath}'.\n\n" +
                            "Use impact_analysis to get the full downstream dependency chain.\n" +
                            "Use trace_dependency in both directions to understand the call graph.\n" +
                            "Use get_edit_context to see the symbol's source code and test coverage.\n" +
                            "Summarize which components and files would be affected and highlight any test coverage gaps."
                    }
                }
            ],
            Description = $"Impact analysis for {symbolPath} (depth {parsedDepth})"
        };
    }

    [McpServerPrompt(Name = "find-related-code", Title = "Find Related Code")]
    [Description("Find code related to a given symbol by traversing dependency relationships.")]
    public static GetPromptResult FindRelatedCode(
        [Description("Fully qualified symbol name to find related code for")] string symbolPath,
        [Description("Relation type filter: 'all', 'calls', 'references', 'inherits', 'implements'")] string? relationType = null)
    {
        var relationHint = relationType is not null
            ? $" with relation type '{relationType}'"
            : "";

        return new GetPromptResult
        {
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text =
                            $"Find code related to '{symbolPath}'{relationHint}.\n\n" +
                            "Use find_related_code to discover related symbols grouped by relation type.\n" +
                            "Use trace_dependency to understand the broader dependency graph.\n" +
                            "Summarize the relationships and highlight key callers and callees."
                    }
                }
            ],
            Description = $"Related code discovery for {symbolPath}"
        };
    }
}
