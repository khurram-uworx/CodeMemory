using CodeMemory.Services.Query;
using CodeMemory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Query;

public sealed class RelationshipQueryServiceTests : BaseServicesTests
{
    static (IStorageService Storage, RelationshipQueryService Service) createServices(string repoRoot, string dbPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<StorageService>>(NullLogger<StorageService>.Instance);
        services.AddCodeMemorySqlliteStorage(repoRoot, $"Data Source={dbPath}");
        var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IStorageService>();
        var svc = new RelationshipQueryService(storage);
        return (storage, svc);
    }

    [Test]
    public async Task GetBySourceAsync_ReturnsRelationships()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "A", TargetSymbolId = "C", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r3", SourceSymbolId = "B", TargetSymbolId = "C", RelationshipType = "References" }
        ]);

        var results = await svc.GetBySourceAsync("A");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["r1", "r2"]));
    }

    [Test]
    public async Task GetByTargetAsync_ReturnsRelationships()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "C", TargetSymbolId = "B", RelationshipType = "References" }
        ]);

        var results = await svc.GetByTargetAsync("B");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["r1", "r2"]));
    }

    [Test]
    public async Task GetAllForSymbolAsync_ReturnsBothDirections()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "B", TargetSymbolId = "C", RelationshipType = "References" },
            new RelationshipRecord { Id = "r3", SourceSymbolId = "C", TargetSymbolId = "A", RelationshipType = "Imports" }
        ]);

        var results = await svc.GetAllForSymbolAsync("A");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["r1", "r3"]));
    }

    [Test]
    public async Task GetBySourceAsync_ReturnsEmpty_WhenNone()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var (storage, svc) = createServices(repoRoot, dbPath);
        await storage.InitializeAsync();

        var results = await svc.GetBySourceAsync("nonexistent");
        Assert.That(results, Is.Empty);
    }
}
