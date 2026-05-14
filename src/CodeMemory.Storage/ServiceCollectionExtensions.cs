using Memori.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

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

    public static IServiceCollection AddCodeMemorySqlliteStorage(this IServiceCollection services,
        string repoRoot, string connectionString,
        int configuredDimension = 1536)
    {
        services.AddSqliteVectorStore(
            _ => connectionString,
            _ => new SqliteVectorStoreOptions());

        services.AddSingleton<IStorageService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<StorageService>>();
            var store = sp.GetRequiredService<VectorStore>();
            var generator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            return new StorageService(repoRoot, logger, store, generator, configuredDimension);
        });

        return services;
    }
}
