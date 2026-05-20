using CodeMemory.Services.Architecture;

namespace CodeMemory.Tests.Services.Architecture;

public sealed class TestComponentResolver : IComponentResolver
{
    public string GetComponentName(string filePath, int depth = 1)
    {
        var normalized = filePath.Replace('\\', '/');
        var trimmed = normalized.TrimStart('/');

        if (depth <= 0)
            return trimmed;

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (depth >= segments.Length)
            return segments.Length > 0 ? segments[^1] : trimmed;

        return string.Join("/", segments.Take(depth));
    }
}
