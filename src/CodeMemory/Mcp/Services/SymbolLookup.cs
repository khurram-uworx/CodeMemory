using CodeMemory.Indexing.Graph;
using CodeMemory.Storage;

namespace CodeMemory.Mcp;

/// <summary>
/// Shared helpers that convert the silent empty-result cases of the dependency tools
/// (symbol not found, no relationships of the requested type) into actionable diagnostics.
/// </summary>
public static class SymbolLookup
{
    /// <summary>
    /// Resolves a symbol path against storage, or null when the path cannot be resolved.
    /// </summary>
    public static async Task<SymbolRecord?> ResolveAsync(IStorageService? storage, string symbolPath,
        CancellationToken ct = default)
        => storage == null ? null : await storage.GetSymbolByFullNameAsync(symbolPath, ct);

    /// <summary>
    /// Returns human-readable suggestions (FullName, Kind, FilePath) for a symbol path
    /// that could not be resolved.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SuggestAsync(IStorageService? storage, string symbolPath,
        int top = 5, CancellationToken ct = default)
    {
        if (storage == null)
            return [];
        var suggestions = await storage.SuggestSymbolsAsync(symbolPath, top, ct);
        return suggestions
            .Select(s => $"{s.FullName} ({s.Kind}, {s.FilePath})")
            .ToList();
    }

    /// <summary>
    /// Distinct relationship types involving a symbol, as a comma-separated string
    /// ("(none)" when the symbol participates in no indexed relationships).
    /// </summary>
    public static async Task<string> AvailableRelationTypesAsync(IStorageService? storage, string symbolId,
        CancellationToken ct = default)
    {
        if (storage == null)
            return "(none)";
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in await storage.GetRelationshipsByTargetAsync(symbolId, ct))
            types.Add(rel.RelationshipType);
        foreach (var rel in await storage.GetRelationshipsBySourceAsync(symbolId, ct))
            types.Add(rel.RelationshipType);
        return types.Count == 0 ? "(none)" : string.Join(", ", types);
    }

    /// <summary>
    /// Wraps a resolved symbol as a self-referencing <see cref="DependencyNode"/> so tools
    /// can report which symbol actually matched a (possibly bare) path.
    /// </summary>
    public static DependencyNode ToNode(SymbolRecord symbol)
        => new(symbol.Name, symbol.FilePath, symbol.Kind, $"{symbol.LineStart}-{symbol.LineEnd}", "self");
}