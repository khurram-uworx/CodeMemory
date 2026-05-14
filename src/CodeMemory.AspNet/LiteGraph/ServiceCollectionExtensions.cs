using CodeMemory.Storage;

namespace CodeMemory.AspNet.LiteGraph;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeMemoryLiteGraphStorage(this IServiceCollection services,
        string repoRoot,
        LiteGraphStorageOptions? options = null)
    {
        services.AddSingleton<IStorageService>(sp =>
        {
            var logger = sp.GetService<ILogger<LiteGraphStorageService>>();
            return new LiteGraphStorageService(repoRoot, options, logger);
        });

        return services;
    }
}
