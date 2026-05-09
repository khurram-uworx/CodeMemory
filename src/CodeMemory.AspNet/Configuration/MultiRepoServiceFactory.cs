using CodeMemory.Storage;

namespace CodeMemory.AspNet.Configuration;

public interface IMultiRepoServiceFactory
{
    IStorageService GetStorageService(string repoName);
}

public sealed class MultiRepoServiceFactory : IMultiRepoServiceFactory
{
    readonly Dictionary<string, IStorageService> services = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string repoName, IStorageService service)
    {
        services[repoName] = service;
    }

    public IStorageService GetStorageService(string repoName)
    {
        if (services.TryGetValue(repoName, out var service))
            return service;

        throw new InvalidOperationException($"No storage service registered for repo '{repoName}'. Available: {string.Join(", ", services.Keys)}");
    }
}
