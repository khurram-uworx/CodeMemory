using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace CodeMemory.AspNet.Storage;

public static class ServiceCollectionExtensions
{
    // we need to hook in memory storage here
    // keeping in mind repoRoot; to support multiple repos

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

    // we need to hook PgVector storage here
    // keeping in mind repoRoot; to support multiple repos, use schemas may be?
}
