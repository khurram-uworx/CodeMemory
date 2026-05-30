namespace CodeMemory.Mcp.Services;

public sealed record EditContextOptions(
    bool IncludeDependencies = true,
    int Depth = 1,
    bool IncludeSourceCode = true,
    int? MaxResults = null,
    int DependencyOffset = 0,
    int RelatedOffset = 0);
