using CodeMemory.Services.Architecture;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Architecture;

public sealed class ComponentClusteringServiceTests : BaseServicesTests
{
    static ComponentClusteringService createService(IStorageService storage)
        => new ComponentClusteringService(storage, NullLogger<ComponentClusteringService>.Instance);

    [Test]
    public async Task GetClustersAsync_ReturnsEmpty_WhenNoSymbols()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var service = createService(storage);
        var clusters = await service.GetClustersAsync();

        Assert.That(clusters, Is.Empty);
    }

    [Test]
    public async Task GetClustersAsync_SingleComponent_ReturnsSingleton()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "c1", Name = "Service", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.Service", LineStart = 1, LineEnd = 10 },
        ]);

        var service = createService(storage);
        var clusters = await service.GetClustersAsync();

        Assert.That(clusters, Has.Count.EqualTo(1));
        Assert.That(clusters[0].Members, Has.Count.EqualTo(1));
        Assert.That(clusters[0].Members[0], Is.EqualTo("src"));
        Assert.That(clusters[0].CohesionScore, Is.EqualTo(1.0));
    }

    [Test]
    public async Task GetClustersAsync_CoupledComponents_AreGrouped()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "s1", Name = "Service", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.Service", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "Config", Kind = "Class", FilePath = "lib/Config.cs", FullName = "Lib.Config", LineStart = 1, LineEnd = 10 },
            new() { Id = "s3", Name = "Util", Kind = "Class", FilePath = "lib/Util.cs", FullName = "Lib.Util", LineStart = 1, LineEnd = 10 },
        ]);

        await storage.StoreRelationshipsAsync([
            new() { Id = "r1", SourceSymbolId = "App.Service", TargetSymbolId = "Lib.Config", RelationshipType = "References" },
            new() { Id = "r2", SourceSymbolId = "App.Service", TargetSymbolId = "Lib.Util", RelationshipType = "References" },
            new() { Id = "r3", SourceSymbolId = "Lib.Config", TargetSymbolId = "App.Service", RelationshipType = "References" },
        ]);

        var service = createService(storage);
        var clusters = await service.GetClustersAsync(0.3);

        var srcCluster = clusters.FirstOrDefault(c => c.Members.Contains("src"));
        Assert.That(srcCluster, Is.Not.Null);
        Assert.That(srcCluster.Members, Does.Contain("lib"));
    }

    [Test]
    public async Task GetClustersAsync_UncoupledComponents_AreSeparate()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "s1", Name = "Service", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.Service", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "Other", Kind = "Class", FilePath = "other/Other.cs", FullName = "Other.App", LineStart = 1, LineEnd = 10 },
            new() { Id = "s3", Name = "Util", Kind = "Class", FilePath = "other/Util.cs", FullName = "Other.Util", LineStart = 1, LineEnd = 10 },
        ]);

        await storage.StoreRelationshipsAsync([
            new() { Id = "r1", SourceSymbolId = "Other.App", TargetSymbolId = "Other.Util", RelationshipType = "References" },
        ]);

        var service = createService(storage);
        var clusters = await service.GetClustersAsync(0.3);

        Assert.That(clusters, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetClustersAsync_HighThreshold_ProducesMoreClusters()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "s1", Name = "Service", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.Service", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "Config", Kind = "Class", FilePath = "lib/Config.cs", FullName = "Lib.Config", LineStart = 1, LineEnd = 10 },
        ]);

        await storage.StoreRelationshipsAsync([
            new() { Id = "r1", SourceSymbolId = "App.Service", TargetSymbolId = "Lib.Config", RelationshipType = "References" },
        ]);

        var service = createService(storage);
        var lowThreshold = await service.GetClustersAsync(0.01);
        var highThreshold = await service.GetClustersAsync(0.99);

        Assert.That(lowThreshold.Count, Is.LessThanOrEqualTo(highThreshold.Count));
    }

    [Test]
    public async Task GetClustersAsync_ThresholdIsClamped()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "s1", Name = "Service", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.Service", LineStart = 1, LineEnd = 10 },
        ]);

        var service = createService(storage);
        var belowMin = await service.GetClustersAsync(0.0);
        var aboveMax = await service.GetClustersAsync(2.0);

        Assert.That(belowMin, Has.Count.EqualTo(1));
        Assert.That(aboveMax, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetClustersAsync_NoRelationships_IslandsForm()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "s1", Name = "Service", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.Service", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "Lib", Kind = "Class", FilePath = "lib/Lib.cs", FullName = "Lib.Lib", LineStart = 1, LineEnd = 10 },
            new() { Id = "s3", Name = "Test", Kind = "Class", FilePath = "tests/Test.cs", FullName = "Tests.Test", LineStart = 1, LineEnd = 10 },
        ]);

        var service = createService(storage);
        var clusters = await service.GetClustersAsync(0.3);

        Assert.That(clusters, Has.Count.EqualTo(3));
        foreach (var cluster in clusters)
        {
            Assert.That(cluster.Members, Has.Count.EqualTo(1));
            Assert.That(cluster.CohesionScore, Is.EqualTo(1.0));
        }
    }
}
