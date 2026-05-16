using Memori.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace CodeMemory.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeMemoryInMemoryStorage(this IServiceCollection services,
        string repoRoot,
        int configuredDimension = 1536)
    {
        var memoryStore = new InMemoryVectorStore();
        services.AddSingleton<VectorStore>(sp => memoryStore);
        services.AddSingleton<IStorageService>(sp =>
        {
            var generator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            var logger = sp.GetRequiredService<ILogger<StorageService>>();

            return new StorageService(repoRoot, logger, memoryStore, generator, configuredDimension);
        });

        return services;
    }
}
