using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Services;
using CodeMemory.AspNet.Storage;
using CodeMemory.Indexing;
using CodeMemory.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeMemory.AspNet.Configuration;

public sealed class StorageBootstrapper
{
    readonly WebApplication app;
    readonly IServiceRegistry storageRegistry;
    readonly RepoRegistryOptions registryOptions;
    readonly IConfiguration configuration;
    readonly string storageProvider;

    public StorageBootstrapper(WebApplication app, string storageProvider)
    {
        this.app = app;
        this.storageProvider = storageProvider;
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
        var cloneIndex = app.Services.GetRequiredService<CloneIndexService>();

        foreach (var (name, source) in configRepos ?? [])
        {
            var isUrl = source.Contains("://");

            var repo = new Repositories
            {
                Name = name,
                GitUrl = isUrl ? source : null,
                LocalPath = CloneIndexService.ResolveRepoPath(source, name, registryOptions),
                CloneStatus = isUrl ? "Pending" : "Cloned",
                IndexStatus = "Pending"
            };

            await repoService.AddAsync(repo);
            await cloneIndex.EnqueueRepoAsync(name, source, null);
        }
    }

    async Task<List<Repositories>> loadAndRegisterReposAsync()
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var repos = await db.RegisteredRepos
            .Where(r => r.CloneStatus == "Cloned")
            .ToListAsync();

        var cloneIndex = app.Services.GetRequiredService<CloneIndexService>();

        foreach (var repo in repos)
        {
            if (cloneIndex.IsProcessing(repo.Name))
                continue;

            var storage = createStorageForProvider(repo);
            storageRegistry.Register(repo.Name, storage);

            if (repo.IndexStatus == "Indexed")
                IndexingState.MarkCompleted(repo.Name);
        }

        return repos;
    }

    IStorageService createStorageForProvider(Repositories repo)
    {
        var factory = app.Services.GetRequiredService<StorageFactory>();
        return factory(repo.Name, repo.LocalPath, repo.Id);
    }

    public async Task<List<Repositories>> BootstrapAsync()
    {
        await ensureDatabaseAsync();
        await seedFromConfigAsync();
        return await loadAndRegisterReposAsync();
    }
}
