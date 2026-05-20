using System.Collections.Concurrent;

namespace CodeMemory.Services.Architecture;

public static class ComponentMapping
{
    static readonly ConcurrentDictionary<string, string> prefixToComponent = new(StringComparer.OrdinalIgnoreCase);
    static volatile bool initialized;

    public static bool IsInitialized => initialized;

    public static string? Resolve(string filePath)
    {
        if (!initialized || prefixToComponent.IsEmpty)
            return null;

        var normalized = filePath.Replace('\\', '/').TrimStart('/');
        string? bestMatch = null;
        var bestLength = 0;

        foreach (var (prefix, component) in prefixToComponent)
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

    public static void Initialize(IReadOnlyDictionary<string, string> mapping)
    {
        prefixToComponent.Clear();
        foreach (var kvp in mapping)
            prefixToComponent[kvp.Key] = kvp.Value;
        initialized = true;
    }

    public static void Clear()
    {
        prefixToComponent.Clear();
        initialized = false;
    }
}
