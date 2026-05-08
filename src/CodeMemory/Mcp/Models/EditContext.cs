using CodeMemory.Indexing.Graph;

namespace CodeMemory.Mcp.Models;

public sealed record TargetInfo(
    string SymbolName,
    string FilePath,
    string LineRange,
    string Kind);

public sealed record EditContext(
    TargetInfo Target,
    string? SourceCode,
    IReadOnlyList<DependencyNode>? Dependencies,
    IReadOnlyList<DependencyNode>? RelatedSymbols,
    IReadOnlyList<string>? Tests,
    DateTimeOffset Timestamp,
    IReadOnlyList<string>? Warnings = null);
