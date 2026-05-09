using CodeMemory.Services;

namespace CodeMemory.AspNet.Services;

public sealed class IndexingHostedService : BackgroundService
{
    readonly IServiceProvider serviceProvider;
    readonly IConfiguration configuration;
    readonly ILogger<IndexingHostedService> logger;

    public IndexingHostedService(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<IndexingHostedService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.configuration = configuration;
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

        var appBasePath = Environment.CurrentDirectory;//AppContext.BaseDirectory;

        foreach (var (name, path) in repositories)
        {
            var repoPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(appBasePath, path));
            logger.LogInformation("Indexing repository '{Name}' at path '{Path}'", name, repoPath);

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

        logger.LogInformation("Indexing hosted service completed");
    }
}
