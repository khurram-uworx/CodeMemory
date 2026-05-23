using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Storage;
using CodeMemory.Indexing;
using CodeMemory.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CodeMemory.AspNet.Configuration;

public sealed class StorageBootstrapper
{
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

    readonly WebApplication app;
    readonly ILoggerFactory loggerFactory;
    readonly IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator;
    readonly IConfiguration configuration;
    readonly IServiceRegistry storageRegistry;
    readonly RepoRegistryOptions registryOptions;
    readonly string storageProvider;

    public StorageBootstrapper(WebApplication app, string storageProvider)
    {
        this.app = app;
        this.storageProvider = storageProvider;
        loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        embeddingGenerator = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        configuration = app.Services.GetRequiredService<IConfiguration>();
        storageRegistry = app.Services.GetRequiredService<IServiceRegistry>();
        registryOptions = app.Services.GetRequiredService<RepoRegistryOptions>();
    }

    async Task ensureDatabaseAsync()
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        // SQLite directory creation is only needed when using SQLite provider
        if (storageProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connString = db.Database.GetConnectionString();
            if (connString is not null)
            {
                var builder = new SqliteConnectionStringBuilder(connString);
                var dataSource = builder.DataSource;
                if (!string.IsNullOrEmpty(dataSource) && dataSource != ":memory:")
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
            }
        }

        await db.Database.EnsureCreatedAsync();
    }

    async Task seedFromConfigAsync()
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (await db.RegisteredRepos.AnyAsync())
            return;

        var configRepos = configuration.GetSection("Repositories").Get<Dictionary<string, string>>();
        var repoService = app.Services.GetRequiredService<RepoRegistryService>();

        foreach (var (name, source) in configRepos ?? [])
        {
            var isUrl = source.Contains("://");
            var resolvedPath = isUrl
                ? Path.GetFullPath(Path.Combine(registryOptions.CloneBasePath, name))
                : Path.IsPathRooted(source)
                    ? source
                    : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, source));

            await repoService.AddAsync(new RegisteredRepo
            {
                Name = name,
                GitUrl = isUrl ? source : null,
                LocalPath = resolvedPath,
                CloneStatus = isUrl ? "Pending" : "Cloned",
                IndexStatus = "Pending",
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    async Task<List<RegisteredRepo>> loadAndRegisterReposAsync()
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var repos = await db.RegisteredRepos
            .Where(r => r.CloneStatus == "Cloned")
            .ToListAsync();

        foreach (var repo in repos)
        {
            var storage = createStorageForProvider(repo.LocalPath, repo.Name);
            storageRegistry.Register(repo.Name, storage);

            if (repo.IndexStatus == "Indexed")
                IndexingState.MarkCompleted(repo.Name);
        }

        return repos;
    }

    IStorageService createStorageForProvider(string repoRoot, string repoName)
    {
        if (string.Equals(storageProvider, "inmemory", StringComparison.OrdinalIgnoreCase))
            return app.Services.CreateInMemoryStorage(
                repoRoot,
                loggerFactory.CreateLogger<StorageService>(),
                embeddingGenerator);

        if (string.Equals(storageProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var memoryPath = Path.Combine(repoRoot, ".codememory");
            Directory.CreateDirectory(memoryPath);
            var connString = $"Data Source={Path.Combine(memoryPath, "sqlvec.db")}";
            return Storage.ServiceCollectionExtensions.createSqliteStorage(
                repoRoot, connString,
                loggerFactory.CreateLogger<HybridStorageService>(),
                embeddingGenerator);
        }

        if (string.Equals(storageProvider, "pgvector", StringComparison.OrdinalIgnoreCase))
        {
            var connString = configuration.GetConnectionString("PgVector")
                ?? throw new InvalidOperationException(
                    "Connection string 'PgVector' is required when Storage:Provider is 'pgvector'");
            var schema = sanitizeSchemaName(repoName);
            return Storage.ServiceCollectionExtensions.CreatePgVectorStorage(
                repoRoot, connString, schema,
                loggerFactory.CreateLogger<HybridStorageService>(),
                embeddingGenerator);
        }

        if (string.Equals(storageProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            var connString = configuration.GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException(
                    "Connection string 'SqlServer' is required when Storage:Provider is 'sqlserver'");
            var schema = sanitizeSchemaName(repoName);
            return Storage.ServiceCollectionExtensions.createSqlServerStorage(
                repoRoot, connString, schema,
                loggerFactory.CreateLogger<HybridStorageService>(),
                embeddingGenerator);
        }

        throw new InvalidOperationException(
            $"Unsupported storage provider '{storageProvider}'. Supported: inmemory, sqlite, pgvector, sqlserver");
    }

    public async Task<List<RegisteredRepo>> BootstrapAsync()
    {
        await ensureDatabaseAsync();
        await seedFromConfigAsync();
        return await loadAndRegisterReposAsync();
    }
}
