using CodeMemory.Services.Graph;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Graph;

public sealed class DependencyGraphServiceTests : BaseServicesTests
{
    static DependencyGraphService createGraphService(IStorageService storage)
        => new DependencyGraphService(storage, NullLogger<DependencyGraphService>.Instance);

    static string makeGuid(string seed)
    {
        var guid = Guid.NewGuid().ToString("N");
        return guid;
    }

    static async Task<SymbolRecord> storeSymbol(IStorageService storage, string fullName, string guid)
    {
        var symbol = new SymbolRecord
        {
            Id = guid,
            Name = fullName,
            Kind = "Class",
            FilePath = $"/src/{fullName}.cs",
            FullName = fullName,
            LineStart = 1,
            LineEnd = 10,
        };
        await storage.StoreSymbolsAsync([symbol]);
        return symbol;
    }

    [Test]
    public async Task TraceAsync_Upstream_ReturnsUpstreamDependencies()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var myClassGuid = makeGuid("MyClass");
        var myLoggerGuid = makeGuid("MyLogger");
        var myConfigGuid = makeGuid("MyConfig");

        await storeSymbol(storage, "MyClass", myClassGuid);
        await storeSymbol(storage, "MyLogger", myLoggerGuid);
        await storeSymbol(storage, "MyConfig", myConfigGuid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = myClassGuid, TargetSymbolId = myLoggerGuid, RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = myClassGuid, TargetSymbolId = myConfigGuid, RelationshipType = "References"
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

        var myServiceGuid = makeGuid("MyService");
        var myBaseClassGuid = makeGuid("MyBaseClass");
        var anotherServiceGuid = makeGuid("AnotherService");

        await storeSymbol(storage, "MyService", myServiceGuid);
        await storeSymbol(storage, "MyBaseClass", myBaseClassGuid);
        await storeSymbol(storage, "AnotherService", anotherServiceGuid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = myServiceGuid, TargetSymbolId = myBaseClassGuid, RelationshipType = "Inherits"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = anotherServiceGuid, TargetSymbolId = myBaseClassGuid, RelationshipType = "Inherits"
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

        var myClassGuid = makeGuid("MyClass");
        var myLoggerGuid = makeGuid("MyLogger");
        var consumerClassGuid = makeGuid("ConsumerClass");

        await storeSymbol(storage, "MyClass", myClassGuid);
        await storeSymbol(storage, "MyLogger", myLoggerGuid);
        await storeSymbol(storage, "ConsumerClass", consumerClassGuid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = myClassGuid, TargetSymbolId = myLoggerGuid, RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = consumerClassGuid, TargetSymbolId = myClassGuid, RelationshipType = "References"
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

        var myClassGuid = makeGuid("MyClass");
        var myLoggerGuid = makeGuid("MyLogger");
        var myConfigGuid = makeGuid("MyConfig");

        await storeSymbol(storage, "MyClass", myClassGuid);
        await storeSymbol(storage, "MyLogger", myLoggerGuid);
        await storeSymbol(storage, "MyConfig", myConfigGuid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = myClassGuid, TargetSymbolId = myLoggerGuid, RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = myLoggerGuid, TargetSymbolId = myConfigGuid, RelationshipType = "References"
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

        var aGuid = makeGuid("A");
        var bGuid = makeGuid("B");

        await storeSymbol(storage, "A", aGuid);
        await storeSymbol(storage, "B", bGuid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = aGuid, TargetSymbolId = bGuid, RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = bGuid, TargetSymbolId = aGuid, RelationshipType = "References"
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

        var rootGuid = makeGuid("Root");
        var l1Guid = makeGuid("L1");
        var l2Guid = makeGuid("L2");
        var l3Guid = makeGuid("L3");
        var l4Guid = makeGuid("L4");

        await storeSymbol(storage, "Root", rootGuid);
        await storeSymbol(storage, "L1", l1Guid);
        await storeSymbol(storage, "L2", l2Guid);
        await storeSymbol(storage, "L3", l3Guid);
        await storeSymbol(storage, "L4", l4Guid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = rootGuid, TargetSymbolId = l1Guid, RelationshipType = "References" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = l1Guid, TargetSymbolId = l2Guid, RelationshipType = "References" },
            new RelationshipRecord { Id = "r3", SourceSymbolId = l2Guid, TargetSymbolId = l3Guid, RelationshipType = "References" },
            new RelationshipRecord { Id = "r4", SourceSymbolId = l3Guid, TargetSymbolId = l4Guid, RelationshipType = "References" },
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

        var myClassGuid = makeGuid("MyClass");
        var loggerGuid = makeGuid("Logger");
        var iConfigGuid = makeGuid("IConfig");

        await storeSymbol(storage, "MyClass", myClassGuid);
        await storeSymbol(storage, "Logger", loggerGuid);
        await storeSymbol(storage, "IConfig", iConfigGuid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = myClassGuid, TargetSymbolId = loggerGuid, RelationshipType = "References" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = myClassGuid, TargetSymbolId = iConfigGuid, RelationshipType = "Implements" },
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

        var myClassGuid = makeGuid("MyClass");
        var loggerGuid = makeGuid("Logger");
        var iConfigGuid = makeGuid("IConfig");

        await storeSymbol(storage, "MyClass", myClassGuid);
        await storeSymbol(storage, "Logger", loggerGuid);
        await storeSymbol(storage, "IConfig", iConfigGuid);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = myClassGuid, TargetSymbolId = loggerGuid, RelationshipType = "References" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = myClassGuid, TargetSymbolId = iConfigGuid, RelationshipType = "Implements" },
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

        var myClassGuid = makeGuid("MyClass");
        await storeSymbol(storage, "MyClass", myClassGuid);

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
