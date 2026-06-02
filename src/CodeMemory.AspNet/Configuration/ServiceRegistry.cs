using CodeMemory.Storage;
using System.Collections.Concurrent;

namespace CodeMemory.AspNet.Configuration;

public interface IServiceRegistry
{
    void Register(string repoName, IStorageService storage);
    bool Unregister(string repoName);
    IStorageService GetStorage(string? repoName);
}

public sealed class ServiceRegistry : IServiceRegistry
{
    readonly ConcurrentDictionary<string, IStorageService> storageServices = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string repoName, IStorageService storage)
        => storageServices[repoName] = storage;

    public bool Unregister(string repoName)
        => storageServices.TryRemove(repoName, out _);

    public IStorageService GetStorage(string? repoName)
    {
        if (repoName is not null && storageServices.TryGetValue(repoName, out var storage))
            return storage;

        if (repoName is null)
        {
            if (storageServices.TryGetValue("default", out var defaultService))
                return defaultService;

            if (storageServices.Count > 0)
                return storageServices.First().Value;

            throw new InvalidOperationException("No storage services registered.");
        }

        throw new InvalidOperationException(
            $"No storage service registered for repo '{repoName}'. " +
            $"Available: {string.Join(", ", storageServices.Keys)}");
    }
}
