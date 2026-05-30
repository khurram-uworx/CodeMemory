namespace CodeMemory.Services.Architecture;

public interface IComponentResolver
{
    Task<string> GetComponentNameAsync(string filePath, int depth = 1, CancellationToken ct = default);
}
