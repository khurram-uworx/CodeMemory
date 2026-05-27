using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Storage.PgVector;
using CodeMemory.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using Microsoft.SemanticKernel.Connectors.SqlServer;

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

    internal static string sanitizeSchemaName(string name)
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

    internal static IStorageService createSqliteStorage(
        string repoRoot,
        int registeredRepoId,
        string connectionString,
        IDbContextFactory<RepoRegistryDbContext> registryDbFactory,
        ILogger logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536,
        IMemoryCache? cache = null)
    {
        var store = new SqliteVectorStore(connectionString);
        return new HybridStorageService(
            repoRoot,
            registeredRepoId,
            logger,
            store,
            createSqliteDbContextFactory(connectionString, "main"),
            registryDbFactory,
            embeddingGenerator,
            configuredDimension,
            cache);
    }

    internal static IStorageService createSqlServerStorage(
        string repoRoot,
        int registeredRepoId,
        string connectionString,
        string schema,
        IDbContextFactory<RepoRegistryDbContext> registryDbFactory,
        ILogger logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536,
        IMemoryCache? cache = null)
    {
        ensureSqlServerSchemaExists(connectionString, schema);

        var options = new SqlServerVectorStoreOptions { Schema = schema };
        var store = new SqlServerVectorStore(connectionString, options);
        return new HybridStorageService(
            repoRoot,
            registeredRepoId,
            logger,
            store,
            createSqlServerDbContextFactory(connectionString, schema),
            registryDbFactory,
            embeddingGenerator,
            configuredDimension,
            cache);
    }

    // public because of tests
    public static IStorageService CreatePgVectorStorage(
        string repoRoot,
        int registeredRepoId,
        string connectionString,
        string schema,
        IDbContextFactory<RepoRegistryDbContext> registryDbFactory,
        ILogger logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536,
        IMemoryCache? cache = null)
    {
        var store = new PgVectorStore(connectionString, new PgVectorOptions { Schema = schema });
        return new HybridStorageService(
            repoRoot,
            registeredRepoId,
            logger,
            store,
            createNpgsqlDbContextFactory(connectionString, schema),
            registryDbFactory,
            embeddingGenerator,
            configuredDimension,
            cache);
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
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int registeredRepoId = 0,
        IDbContextFactory<RepoRegistryDbContext>? registryDbFactory = null)
    {
        var useSqlite = string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase);
        var usePgVector = string.Equals(provider, "pgvector", StringComparison.OrdinalIgnoreCase);
        var useSqlServer = string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase);

        var useInMemory = string.Equals(provider, "inmemory", StringComparison.OrdinalIgnoreCase);

        if (!useInMemory && !useSqlite && !usePgVector && !useSqlServer)
            throw new InvalidOperationException(
                $"Unsupported storage provider '{provider}'. Supported: inmemory, sqlite, pgvector, sqlserver");

        IStorageService? storageService = null;
        string? dbPath = null;

        if (usePgVector)
        {
            var pgConnectionString = builder.Configuration.GetConnectionString("PgVector")
                ?? throw new InvalidOperationException("PgVector: Connection string 'PgVector' is required when Storage:Provider is 'pgvector'");
            var schema = sanitizeSchemaName(name);
            storageService = CreatePgVectorStorage(
                repoRoot, registeredRepoId, pgConnectionString, schema, registryDbFactory!,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
            dbPath = $"pgvector://{schema}";
        }
        else if (useSqlServer)
        {
            var sqlServerConnectionString = builder.Configuration.GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException("SQL Server: Connection string 'SqlServer' is required when Storage:Provider is 'sqlserver'");
            var schema = sanitizeSchemaName(name);
            storageService = createSqlServerStorage(
                repoRoot, registeredRepoId, sqlServerConnectionString, schema, registryDbFactory!,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
            dbPath = $"sqlserver://{schema}";
        }
        else if (useSqlite)
        {
            var memoryPath = Path.Combine(repoRoot, ".codememory");
            Directory.CreateDirectory(memoryPath);

            var sqliteConnectionString = $"Data Source={Path.Combine(memoryPath, "sqlvec.db")};Cache=Shared";
            storageService = createSqliteStorage(
                repoRoot, registeredRepoId, sqliteConnectionString, registryDbFactory!,
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
