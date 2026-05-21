using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Registry.Models;
using CodeMemory.Indexing;
using CodeMemory.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CodeMemory.AspNet.Configuration;

public sealed class StorageBootstrapper
{
    readonly WebApplication app;
    readonly ILoggerFactory loggerFactory;
    readonly IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator;
    readonly IConfiguration configuration;
    readonly IServiceRegistry storageRegistry;
    readonly RepoRegistryOptions registryOptions;

    public StorageBootstrapper(WebApplication app)
    {
        this.app = app;
        loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        embeddingGenerator = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        configuration = app.Services.GetRequiredService<IConfiguration>();
        storageRegistry = app.Services.GetRequiredService<IServiceRegistry>();
        registryOptions = app.Services.GetRequiredService<RepoRegistryOptions>();
    }

    public async Task<List<RegisteredRepo>> BootstrapAsync()
    {
        await EnsureDatabaseAsync();
        await SeedFromConfigAsync();
        return await LoadAndRegisterReposAsync();
    }

    async Task EnsureDatabaseAsync()
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var connString = db.Database.GetConnectionString();
        if (connString is not null && registryOptions.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
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

        await db.Database.EnsureCreatedAsync();
    }

    async Task SeedFromConfigAsync()
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

    async Task<List<RegisteredRepo>> LoadAndRegisterReposAsync()
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var repos = await db.RegisteredRepos
            .Where(r => r.CloneStatus == "Cloned")
            .ToListAsync();

        foreach (var repo in repos)
        {
            var storage = app.Services.CreateInMemoryStorage(
                repo.LocalPath,
                loggerFactory.CreateLogger<StorageService>(),
                embeddingGenerator);

            storageRegistry.Register(repo.Name, storage);

            if (repo.IndexStatus == "Indexed")
                IndexingState.MarkCompleted(repo.Name);
        }

        return repos;
    }
}
