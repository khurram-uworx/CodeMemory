namespace CodeMemory.Indexing.Architecture;

public sealed record ComponentInfo(string Name, int FileCount, int SymbolCount);

public sealed record ArchitectureOverview(
    IReadOnlyList<ComponentInfo> TopLevelComponents,
    IReadOnlyDictionary<string, int> LanguageBreakdown,
    int TotalFiles,
    int TotalSymbols);

public interface IArchitectureService
{
    Task<ArchitectureOverview> GetOverviewAsync(string? path = null, CancellationToken ct = default);
}
