using CodeMemory.Services.Graph;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Graph;

public sealed class DependencyGraphServiceTests : BaseServicesTests
{
    static DependencyGraphService createGraphService(IStorageService storage)
        => new DependencyGraphService(storage, NullLogger<DependencyGraphService>.Instance);

    [Test]
    public async Task TraceAsync_Upstream_ReturnsUpstreamDependencies()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = "MyClass", TargetSymbolId = "MyLogger", RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = "MyClass", TargetSymbolId = "MyConfig", RelationshipType = "References"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("MyClass", "upstream", 1);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Any(n => n.SymbolName == "MyLogger"), Is.True);
        Assert.That(result.Any(n => n.SymbolName == "MyConfig"), Is.True);
    }

    [Test]
    public async Task TraceAsync_Downstream_ReturnsDownstreamDependencies()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = "MyService", TargetSymbolId = "MyBaseClass", RelationshipType = "Inherits"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = "AnotherService", TargetSymbolId = "MyBaseClass", RelationshipType = "Inherits"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("MyBaseClass", "downstream", 1);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Any(n => n.SymbolName == "MyService"), Is.True);
        Assert.That(result.Any(n => n.SymbolName == "AnotherService"), Is.True);
    }

    [Test]
    public async Task TraceAsync_Both_ReturnsAllRelationships()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = "MyClass", TargetSymbolId = "MyLogger", RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = "ConsumerClass", TargetSymbolId = "MyClass", RelationshipType = "References"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("MyClass", "both", 1);

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TraceAsync_MultiDepth_FollowsChain()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = "MyClass", TargetSymbolId = "MyLogger", RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = "MyLogger", TargetSymbolId = "MyConfig", RelationshipType = "References"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("MyClass", "upstream", 2);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Any(n => n.SymbolName == "MyLogger"), Is.True);
        Assert.That(result.Any(n => n.SymbolName == "MyConfig"), Is.True);
    }

    [Test]
    public async Task TraceAsync_CircularReference_DoesNotLoop()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = "B", TargetSymbolId = "A", RelationshipType = "References"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("A", "upstream", 3);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Any(n => n.SymbolName == "B"), Is.True);
        Assert.That(result.Any(n => n.SymbolName == "A"), Is.True);
    }

    [Test]
    public async Task TraceAsync_DepthIsCappedAtThree()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "Root", TargetSymbolId = "L1", RelationshipType = "References" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "L1", TargetSymbolId = "L2", RelationshipType = "References" },
            new RelationshipRecord { Id = "r3", SourceSymbolId = "L2", TargetSymbolId = "L3", RelationshipType = "References" },
            new RelationshipRecord { Id = "r4", SourceSymbolId = "L3", TargetSymbolId = "L4", RelationshipType = "References" },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("Root", "upstream", 5);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task FindRelatedAsync_ReturnsAllTypes_WhenRelationTypeIsAll()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "MyClass", TargetSymbolId = "Logger", RelationshipType = "References" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "MyClass", TargetSymbolId = "IConfig", RelationshipType = "Implements" },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.FindRelatedAsync("MyClass", "all");

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task FindRelatedAsync_FiltersByType()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "MyClass", TargetSymbolId = "Logger", RelationshipType = "References" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "MyClass", TargetSymbolId = "IConfig", RelationshipType = "Implements" },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.FindRelatedAsync("MyClass", "Implements");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SymbolName, Is.EqualTo("IConfig"));
    }

    [Test]
    public async Task FindTestCoverageAsync_ReturnsEmpty_WhenNoRelationships()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var graph = createGraphService(storage);
        var result = await graph.FindTestCoverageAsync("MyClass");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task TraceAsync_UnknownSymbol_ReturnsEmpty()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("NonExistent", "both", 1);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task FindRelatedAsync_UnknownSymbol_ReturnsEmpty()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var graph = createGraphService(storage);
        var result = await graph.FindRelatedAsync("NonExistent", "all");

        Assert.That(result, Is.Empty);
    }
}
