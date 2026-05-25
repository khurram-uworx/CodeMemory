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

    static string? resolveFromMapping(string filePath, IReadOnlyList<ComponentInformation> components)
    {
        if (components.Count == 0)
            return null;

        var normalized = filePath.Replace('\\', '/').TrimStart('/');
        string? bestMatch = null;
        var bestLength = 0;

        foreach (var component in components)
        {
            var normalizedPrefix = component.BuildFilePath.Replace('\\', '/').TrimEnd('/') + '/';
            if (normalized.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                && normalizedPrefix.Length > bestLength)
            {
                bestMatch = component.ComponentName;
                bestLength = normalizedPrefix.Length;
            }
        }

        return bestMatch;
    }

    public async Task<string> GetComponentNameAsync(string filePath, int depth = 1)
    {
        var components = await storage.LoadComponentMappingAsync();

        var fromMapping = resolveFromMapping(filePath, components);
        if (fromMapping != null)
            return fromMapping;

        return getDirectoryAtDepth(filePath, depth);
    }
}
