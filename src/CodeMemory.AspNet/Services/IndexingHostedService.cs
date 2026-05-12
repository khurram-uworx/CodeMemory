using CodeMemory.AspNet.Configuration;
using CodeMemory.Services;

namespace CodeMemory.AspNet.Services;

public sealed class IndexingHostedService : BackgroundService
{
    readonly IServiceProvider serviceProvider;
    readonly IConfiguration configuration;
    readonly IRepoContextAccessor repoContext;
    readonly ILogger<IndexingHostedService> logger;

    public IndexingHostedService(IServiceProvider serviceProvider, IConfiguration configuration,
        IRepoContextAccessor repoContext, ILogger<IndexingHostedService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.configuration = configuration;
        this.repoContext = repoContext;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Indexing hosted service starting");

        var repositories = configuration.GetSection("Repositories").Get<Dictionary<string, string>>()
            ?? new Dictionary<string, string>();

        if (repositories.Count == 0)
        {
            logger.LogWarning("No repositories configured in appsettings.json:Repositories");
            return;
        }

        var appBasePath = Environment.CurrentDirectory;

        // Initialize all storage services upfront so MCP tools can query any repo immediately
        var registry = serviceProvider.GetRequiredService<IServiceRegistry>();
        foreach (var (name, _) in repositories)
        {
            try
            {
                var storage = registry.GetStorage(name);
                await storage.InitializeAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize storage for repository '{Name}'", name);
            }
        }

        try
        {
            foreach (var (name, path) in repositories)
            {
                var repoPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(appBasePath, path));
                logger.LogInformation("Indexing repository '{Name}' at path '{Path}'", name, repoPath);

                repoContext.CurrentRepoName = name;
                repoContext.CurrentRepoRoot = repoPath;

                using var scope = serviceProvider.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();

                try
                {
                    await engine.RunIndexingAsync(repoPath, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Indexing cancelled for repository '{Name}'", name);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error indexing repository '{Name}'", name);
                }
            }
        }
        finally
        {
            repoContext.CurrentRepoName = null;
            repoContext.CurrentRepoRoot = null;
        }

        logger.LogInformation("Indexing hosted service completed");
    }
}
