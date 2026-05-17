using CodeMemory.Storage;
using Memori.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.PgVector;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using Npgsql;

namespace CodeMemory.AspNet.Storage;

public static class ServiceCollectionExtensions
{
    public static IStorageService CreateInMemoryStorage(
        string repoRoot,
        ILogger<StorageService> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        var store = new InMemoryVectorStore();
        return new StorageService(repoRoot, logger, store, embeddingGenerator, configuredDimension);
    }

    public static IStorageService CreateSqliteStorage(
        string repoRoot,
        string connectionString,
        ILogger<StorageService> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        var store = new SqliteVectorStore(connectionString);
        return new StorageService(repoRoot, logger, store, embeddingGenerator, configuredDimension);
    }

    public static IStorageService CreatePgVectorStorage(
        string repoRoot,
        string connectionString,
        string schema,
        ILogger<StorageService> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        // Ensure the per-repo schema exists
        using (var conn = dataSource.CreateConnection())
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"";
            cmd.ExecuteNonQuery();
        }

        var options = new PostgresVectorStoreOptions { Schema = schema };
        var store = new PostgresVectorStore(dataSource, ownsDataSource: true, options);
        return new StorageService(repoRoot, logger, store, embeddingGenerator, configuredDimension);
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
