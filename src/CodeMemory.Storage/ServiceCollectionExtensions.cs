using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace CodeMemory.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeMemoryStorage(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSqliteVectorStore(
            _ => connectionString,
            _ => new SqliteVectorStoreOptions());

        services.AddSingleton<IStorageService, StorageService>();

        return services;
    }
}
