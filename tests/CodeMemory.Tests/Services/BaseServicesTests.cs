
using CodeMemory.AspNet.Storage;
using CodeMemory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services;

public abstract class BaseServicesTests
{
    protected (string, string) GetTempDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, "test.db"));
    }

    protected IStorageService CreateStorage(string repoRoot, string dbPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<StorageService>>(NullLogger<StorageService>.Instance);
        services.AddCodeMemorySqlliteStorage(repoRoot, $"Data Source={dbPath}");
        return services.BuildServiceProvider().GetRequiredService<IStorageService>();
    }
}
