using CodeMemory.Services.Architecture;

namespace CodeMemory.Tests.Services.Architecture;

public sealed class TestComponentResolver : IComponentResolver
{
    public Task<string> GetComponentNameAsync(string filePath, int depth = 1)
    {
        var normalized = filePath.Replace('\\', '/');
        var trimmed = normalized.TrimStart('/');

        if (depth <= 0)
            return Task.FromResult(trimmed);

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (depth >= segments.Length)
            return Task.FromResult(segments.Length > 0 ? segments[^1] : trimmed);

        var result = string.Join("/", segments.Take(depth));
        return Task.FromResult(result);
    }
}
