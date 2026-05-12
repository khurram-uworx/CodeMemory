using CodeMemory.Services.Query;
using CodeMemory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Query;

public sealed class SymbolQueryServiceTests : BaseServicesTests
{
    static (IStorageService Storage, SymbolQueryService Service) createServices(string repoRoot, string dbPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<StorageService>>(NullLogger<StorageService>.Instance);
        services.AddCodeMemorySqlliteStorage(repoRoot, $"Data Source={dbPath}");
        var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IStorageService>();
        var svc = new SymbolQueryService(storage);
        return (storage, svc);
    }

    [Test]
    public async Task GetByIdAsync_ReturnsSymbol()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "MyClass", Name = "MyClass", Kind = "Class",
                FilePath = "/src/My.cs", LineStart = 1, LineEnd = 30, FullName = "MyClass" }
        ]);

        var result = await svc.GetByIdAsync("MyClass");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("MyClass"));
    }

    [Test]
    public async Task GetByIdAsync_ReturnsNull_WhenMissing()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        var result = await svc.GetByIdAsync("nonexistent");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetByFileAsync_ReturnsSymbolsInFile()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "A", Name = "A", Kind = "Class",
                FilePath = "/src/Alpha.cs", LineStart = 1, LineEnd = 10, FullName = "A" },
            new SymbolRecord { Id = "B", Name = "B", Kind = "Method",
                FilePath = "/src/Beta.cs", LineStart = 1, LineEnd = 10, FullName = "B" },
            new SymbolRecord { Id = "C", Name = "C", Kind = "Class",
                FilePath = "/src/Alpha.cs", LineStart = 20, LineEnd = 30, FullName = "C" }
        ]);

        var results = await svc.GetByFileAsync("/src/Alpha.cs");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["A", "C"]));
    }

    [Test]
    public async Task GetByKindAsync_ReturnsSymbolsOfKind()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "A", Name = "A", Kind = "Class",
                FilePath = "/src/A.cs", LineStart = 1, LineEnd = 10, FullName = "A" },
            new SymbolRecord { Id = "B", Name = "B", Kind = "Method",
                FilePath = "/src/B.cs", LineStart = 1, LineEnd = 10, FullName = "B" },
            new SymbolRecord { Id = "C", Name = "C", Kind = "Class",
                FilePath = "/src/C.cs", LineStart = 1, LineEnd = 10, FullName = "C" }
        ]);

        var results = await svc.GetByKindAsync("Class");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["A", "C"]));
    }
}
