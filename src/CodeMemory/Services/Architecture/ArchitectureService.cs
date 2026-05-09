using CodeMemory.Indexing.Architecture;
using CodeMemory.Storage.Services;

namespace CodeMemory.Services.Architecture;

public sealed class ArchitectureService : IArchitectureService
{
    static string getTopLevelDirectory(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        var trimmed = normalized.TrimStart('/');
        var slashIndex = trimmed.IndexOf('/');
        return slashIndex > 0 ? trimmed[..slashIndex] : trimmed;
    }

    static readonly HashSet<string> knownKinds =
    [
        "Class", "Interface", "Struct", "Enum", "Record",
        "Method", "Property", "Field", "Event",
    ];

    readonly IStorageService storage;
    readonly ILogger<ArchitectureService> logger;

    public ArchitectureService(IStorageService storage, ILogger<ArchitectureService> logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    public async Task<ArchitectureOverview> GetOverviewAsync(
        string? path = null, CancellationToken ct = default)
    {
        var allSymbols = new List<Storage.Models.SymbolRecord>();

        foreach (var kind in knownKinds)
        {
            var symbols = await storage.GetSymbolsByKindAsync(kind, 100000, ct);
            allSymbols.AddRange(symbols);
        }

        var filtered = path != null
            ? allSymbols.Where(s => s.FilePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList()
            : allSymbols;

        var totalSymbols = filtered.Count;
        var distinctFiles = filtered.Select(s => s.FilePath).Distinct().ToList();
        var totalFiles = distinctFiles.Count;

        var languageBreakdown = new Dictionary<string, int>();
        foreach (var file in distinctFiles)
        {
            var ext = Path.GetExtension(file)?.TrimStart('.').ToLowerInvariant() ?? "unknown";
            var lang = ext switch
            {
                "cs" => "C#",
                "js" => "JavaScript",
                "ts" => "TypeScript",
                "py" => "Python",
                "go" => "Go",
                "rs" => "Rust",
                "java" => "Java",
                "rb" => "Ruby",
                "fs" => "F#",
                "sql" => "SQL",
                _ => ext,
            };
            languageBreakdown.TryGetValue(lang, out var count);
            languageBreakdown[lang] = count + 1;
        }

        var componentFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var componentSymbolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in filtered)
        {
            var component = getTopLevelDirectory(symbol.FilePath);
            if (!componentFiles.ContainsKey(component))
                componentFiles[component] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            componentFiles[component].Add(symbol.FilePath);
            componentSymbolCounts.TryGetValue(component, out var count);
            componentSymbolCounts[component] = count + 1;
        }

        var components = componentFiles
            .Select(c => new ComponentInfo(
                c.Key,
                c.Value.Count,
                componentSymbolCounts.GetValueOrDefault(c.Key, 0)))
            .OrderByDescending(c => c.SymbolCount)
            .ToList();

        logger.LogDebug("Architecture overview: {Components} components, {Files} files, {Symbols} symbols",
            components.Count, totalFiles, totalSymbols);

        return new ArchitectureOverview(components, languageBreakdown, totalFiles, totalSymbols);
    }
}
