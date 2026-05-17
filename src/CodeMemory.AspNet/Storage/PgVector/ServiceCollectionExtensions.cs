using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace CodeMemory.AspNet.Storage.PgVector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeMemoryPgVectorStorage(
        this IServiceCollection services,
        string repoRoot,
        PgVectorStore store,
        int configuredDimension = 1536)
    {
        services.AddSingleton<VectorStore>(store);
        services.AddSingleton<IStorageService>(sp =>
        {
            var generator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            var logger = sp.GetRequiredService<ILogger<StorageService>>();
            return new StorageService(repoRoot, logger, store, generator, configuredDimension);
        });

        return services;
    }
}
