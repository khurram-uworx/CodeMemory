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
            var embeddingGenerator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            var logger = sp.GetRequiredService<ILogger<StorageService>>();

            return new StorageService(repoRoot, logger, memoryStore, embeddingGenerator, configuredDimension);
        });

        return services;
    }

    public static IStorageService CreateInMemoryStorage(this IServiceProvider provider,
        string repoRoot,
        ILogger<StorageService> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        var store = new InMemoryVectorStore();
        return new StorageService(repoRoot, logger, store, embeddingGenerator, configuredDimension);
    }

}
