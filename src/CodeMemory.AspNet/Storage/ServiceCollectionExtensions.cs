using CodeMemory.AspNet.Storage.PgVector;
using CodeMemory.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.PgVector;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using Microsoft.SemanticKernel.Connectors.SqlServer;
using Npgsql;

namespace CodeMemory.AspNet.Storage;

public static class ServiceCollectionExtensions
{
    static string quoteSqlServerIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]")}]";

    static void ensureSqlServerSchemaExists(string connectionString, string schema)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema)
                EXEC('CREATE SCHEMA {quoteSqlServerIdentifier(schema)}')
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.ExecuteNonQuery();
    }

    static string sanitizeSchemaName(string name)
    {
        var sanitized = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
                sanitized.Append(ch);
            else
                sanitized.Append('_');
        }
        var result = sanitized.ToString();
        return string.IsNullOrEmpty(result) ? "default" : result.ToLowerInvariant();
    }

    static IStorageService createSqliteStorage(
        string repoRoot,
        string connectionString,
        ILogger logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        var store = new SqliteVectorStore(connectionString);
        return new HybridStorageService(
            repoRoot,
            logger,
            store,
            createSqliteDbContextFactory(connectionString, "main"),
            embeddingGenerator,
            configuredDimension);
    }

    static IStorageService createSqlServerStorage(
        string repoRoot,
        string connectionString,
        string schema,
        ILogger logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        ensureSqlServerSchemaExists(connectionString, schema);

        var options = new SqlServerVectorStoreOptions { Schema = schema };
        var store = new SqlServerVectorStore(connectionString, options);
        return new HybridStorageService(
            repoRoot,
            logger,
            store,
            createSqlServerDbContextFactory(connectionString, schema),
            embeddingGenerator,
            configuredDimension);
    }

    // public because of tests
    public static IStorageService CreatePgVectorStorage(
        string repoRoot,
        string connectionString,
        string schema,
        ILogger logger,
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
        return new HybridStorageService(
            repoRoot,
            logger,
            store,
            createNpgsqlDbContextFactory(connectionString, schema),
            embeddingGenerator,
            configuredDimension);
    }

    static Func<CodeMemoryDbContext> createNpgsqlDbContextFactory(string connectionString, string schema)
    {
        var options = new DbContextOptionsBuilder<CodeMemoryDbContext>()
            .UseNpgsql(connectionString)
            .ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>()
            .Options;

        return () => new CodeMemoryDbContext(options, schema);
    }

    static Func<CodeMemoryDbContext> createSqlServerDbContextFactory(string connectionString, string schema)
    {
        var options = new DbContextOptionsBuilder<CodeMemoryDbContext>()
            .UseSqlServer(connectionString)
            .ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>()
            .Options;

        return () => new CodeMemoryDbContext(options, schema);
    }

    static Func<CodeMemoryDbContext> createSqliteDbContextFactory(string connectionString, string schema)
    {
        var options = new DbContextOptionsBuilder<CodeMemoryDbContext>()
            .UseSqlite(connectionString)
            .ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>()
            .Options;

        return () => new CodeMemoryDbContext(options, schema);
    }

    public static (string, string?, IStorageService?) CreateStorage(this WebApplicationBuilder builder,
        string provider,
        string name, string repoRoot,
        ILoggerFactory loggerFactory,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        var useSqlite = string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase);
        var usePgVector = string.Equals(provider, "pgvector", StringComparison.OrdinalIgnoreCase);
        var useSqlServer = string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase);

        if (!useSqlite && !usePgVector && !useSqlServer)
            provider = "inmemory";

        IStorageService? storageService = null;
        string? dbPath = null;

        if (usePgVector)
        {
            var pgConnectionString = builder.Configuration.GetConnectionString("PgVector")
                ?? throw new InvalidOperationException("PgVector: Connection string 'PgVector' is required when Storage:Provider is 'pgvector'");
            var schema = sanitizeSchemaName(name);
            storageService = CreatePgVectorStorage(
                repoRoot, pgConnectionString, schema,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
            dbPath = $"pgvector://{schema}";

            //var pgOptions = builder.Configuration.GetSection("PgVector").Get<PgVectorOptions>() ?? new();
            //var store = new PgVectorStore(connString, pgOptions with { ConnectionString = connString });
            //var storageService = new StorageService(repoRoot, logger, store, embeddingGenerator);
            //storageRegistry.Register(name, storageService);
            //repoInfos.Add((name, repoRoot, connString));
        }
        else if (useSqlServer)
        {
            var sqlServerConnectionString = builder.Configuration.GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException("SQL Server: Connection string 'SqlServer' is required when Storage:Provider is 'sqlserver'");
            var schema = sanitizeSchemaName(name);
            storageService = createSqlServerStorage(
                repoRoot, sqlServerConnectionString, schema,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
            dbPath = $"sqlserver://{schema}";
        }
        else if (useSqlite)
        {
            var memoryPath = Path.Combine(repoRoot, ".memorycode");
            Directory.CreateDirectory(memoryPath);

            var sqliteConnectionString = $"Data Source={Path.Combine(memoryPath, "sqlvec.db")}";
            storageService = createSqliteStorage(
                repoRoot, sqliteConnectionString,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
            dbPath = Path.Combine(memoryPath, "sqlvec.db");
        }

        return (provider, dbPath, storageService);
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

    public static IServiceCollection AddCodeMemorySqlServerStorage(this IServiceCollection services,
        string repoRoot, string connectionString, string schema,
        int configuredDimension = 1536)
    {
        ensureSqlServerSchemaExists(connectionString, schema);

        services.AddSqlServerVectorStore(
            _ => connectionString,
            _ => new SqlServerVectorStoreOptions { Schema = schema });

        services.AddSingleton<IStorageService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<StorageService>>();
            var store = sp.GetRequiredService<VectorStore>();
            var generator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            return new StorageService(repoRoot, logger, store, generator, configuredDimension);
        });

        return services;
    }

    public static IServiceCollection AddCodeMemoryPgVectorStorage(this IServiceCollection services,
        string repoRoot, PgVectorStore store,
        int configuredDimension = 1536)
    {
        // repoRoot is not considered in PgVectorStore, schemas ?

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
