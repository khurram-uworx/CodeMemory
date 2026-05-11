using CodeMemory.Storage;
using System.Collections.Concurrent;

namespace CodeMemory.AspNet.Configuration;

public interface IStorageServiceRegistry
{
    void Register(string repoName, IStorageService storage);
    IStorageService GetStorage(string? repoName);
}

public sealed class StorageServiceRegistry : IStorageServiceRegistry
{
    readonly ConcurrentDictionary<string, IStorageService> services = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string repoName, IStorageService storage)
    {
        services[repoName] = storage;
    }

    public IStorageService GetStorage(string? repoName)
    {
        if (repoName is not null && services.TryGetValue(repoName, out var service))
            return service;

        if (repoName is null)
        {
            if (services.TryGetValue("default", out var defaultService))
                return defaultService;

            if (services.Count > 0)
                return services.First().Value;

            throw new InvalidOperationException("No storage services registered. Register at least one repository.");
        }

        throw new InvalidOperationException(
            $"No storage service registered for repo '{repoName}'. Available: {string.Join(", ", services.Keys)}");
    }
}