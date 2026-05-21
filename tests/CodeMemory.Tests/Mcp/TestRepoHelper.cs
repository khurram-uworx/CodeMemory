using CodeMemory.AspNet.Configuration;
using CodeMemory.Storage;
using Memori.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Tests.Mcp;

public static class TestRepoHelper
{
    public static async Task RegisterRepoAsync(this WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IServiceRegistry>();
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var storage = new StorageService(".", loggerFactory.CreateLogger<StorageService>(),
            new InMemoryVectorStore(), embedding);
        await storage.InitializeAsync();

        registry.Register("codememory", storage);
    }
}
