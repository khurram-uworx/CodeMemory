namespace CodeMemory.Services.Architecture;

public sealed class ComponentResolver : IComponentResolver
{
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

    public string GetComponentName(string filePath, int depth = 1)
    {
        var fromMapping = ComponentMapping.Resolve(filePath);
        if (fromMapping != null)
            return fromMapping;

        return getDirectoryAtDepth(filePath, depth);
    }
}
