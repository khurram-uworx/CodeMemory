using CodeMemory.Storage;
using System.Collections.Concurrent;

namespace CodeMemory.AspNet.Configuration;

public interface IServiceRegistry
{
    void Register(string repoName, IStorageService storage);
    IStorageService GetStorage(string? repoName);
}

public sealed class ServiceRegistry : IServiceRegistry
{
    readonly ConcurrentDictionary<string, IStorageService> storageServices = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string repoName, IStorageService storage)
        => storageServices[repoName] = storage;

    T getService<T>(string? repoName) where T : class
    {
        ConcurrentDictionary<string, T> services = typeof(T) switch
        {
            var t when t == typeof(IStorageService) =>
            (ConcurrentDictionary<string, T>)(object)storageServices,
            _ => throw new NotSupportedException($"Service type '{typeof(T).Name}' is not supported.")
        };

        if (repoName is not null && services.TryGetValue(repoName, out var service))
            return service;
        else if (repoName is null)
        {
            if (services.TryGetValue("default", out var defaultService))
                return defaultService;

            if (services.Count > 0)
                return services.First().Value;

            throw new InvalidOperationException($"No {typeof(T).Name} services registered.");
        }

        throw new InvalidOperationException(
            $"No {typeof(T).Name} registered for repo '{repoName}'. " +
            $"Available: {string.Join(", ", services.Keys)}");
    }

    public IStorageService GetStorage(string? repoName)
        => getService<IStorageService>(repoName);
}
