using CodeMemory.Storage;

namespace CodeMemory.Services.Architecture;

public sealed class ComponentResolver : IComponentResolver
{
    readonly IStorageService storage;

    public ComponentResolver(IStorageService storage)
    {
        this.storage = storage;
    }

    static string getDirectoryAtDepth(string filePath, int depth)
    {
        var normalized = filePath.Replace('\\', '/');
        var trimmed = normalized.TrimStart('/');

        if (depth <= 0)
            return trimmed;

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (depth >= segments.Length)
            return segments.Length > 0 ? segments[^1] : trimmed;

        var result = string.Join("/", segments.Take(depth));
        return result;
    }

    static string? resolveFromMapping(string filePath, IReadOnlyDictionary<string, string> mapping)
    {
        if (mapping.Count == 0)
            return null;

        var normalized = filePath.Replace('\\', '/').TrimStart('/');
        string? bestMatch = null;
        var bestLength = 0;

        foreach (var (prefix, component) in mapping)
        {
            var normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/') + '/';
            if (normalized.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                && normalizedPrefix.Length > bestLength)
            {
                bestMatch = component;
                bestLength = normalizedPrefix.Length;
            }
        }

        return bestMatch;
    }

    public async Task<string> GetComponentNameAsync(string filePath, int depth = 1)
    {
        var mapping = await storage.LoadComponentMappingAsync();

        var fromMapping = resolveFromMapping(filePath, mapping);
        if (fromMapping != null)
            return fromMapping;

        return getDirectoryAtDepth(filePath, depth);
    }
}
