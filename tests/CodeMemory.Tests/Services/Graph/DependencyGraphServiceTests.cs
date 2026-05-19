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
    public async Task FindRelatedAsync_IncludesChildMethodRelationships()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var serviceGuid = makeGuid("Service");
        var loggerGuid = makeGuid("Logger");
        var execMethodGuid = makeGuid("Service.Execute");

        await storeSymbol(storage, "Service", serviceGuid);
        await storeSymbol(storage, "Logger", loggerGuid);
        await storeSymbol(storage, "Service.Execute", execMethodGuid);

        // Service class has no direct relationships
        // Service.Execute method calls Logger
        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = execMethodGuid, TargetSymbolId = loggerGuid, RelationshipType = "Calls"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.FindRelatedAsync("Service", "all");

        // Should find Logger through child method propagation
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SymbolName, Is.EqualTo("Logger"));
        Assert.That(result[0].RelationType, Is.EqualTo("Calls"));
    }

    [Test]
    public async Task FindRelatedAsync_IncludesChildFieldRelationships()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var serviceGuid = makeGuid("Service");
        var configGuid = makeGuid("Config");
        var configFieldGuid = makeGuid("Service._config");

        await storeSymbol(storage, "Service", serviceGuid);
        await storeSymbol(storage, "Config", configGuid);
        await storeSymbol(storage, "Service._config", configFieldGuid);

        // Service class has no direct relationships
        // Service._config field references Config
        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = configFieldGuid, TargetSymbolId = configGuid, RelationshipType = "References"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.FindRelatedAsync("Service", "all");

        // Should find Config through child field propagation
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SymbolName, Is.EqualTo("Config"));
        Assert.That(result[0].RelationType, Is.EqualTo("References"));
    }

    [Test]
    public async Task FindRelatedAsync_DeduplicatesAcrossParentAndChild()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var serviceGuid = makeGuid("Service");
        var loggerGuid = makeGuid("Logger");
        var execMethodGuid = makeGuid("Service.Execute");

        await storeSymbol(storage, "Service", serviceGuid);
        await storeSymbol(storage, "Logger", loggerGuid);
        await storeSymbol(storage, "Service.Execute", execMethodGuid);

        // Service class directly references Logger
        // Service.Execute method also calls Logger (different relationship, same target)
        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = serviceGuid, TargetSymbolId = loggerGuid, RelationshipType = "References"
            },
            new RelationshipRecord
            {
                Id = "r2", SourceSymbolId = execMethodGuid, TargetSymbolId = loggerGuid, RelationshipType = "Calls"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.FindRelatedAsync("Service", "all");

        // Both relationships have different IDs, so both should appear
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(n => n.SymbolName == "Logger"), Is.True);
    }

    [Test]
    public async Task FindRelatedAsync_DoesNotDuplicateWhenParentVisitsChildTarget()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var parentGuid = makeGuid("Parent");
        var childGuid = makeGuid("Parent.Child");
        var targetGuid = makeGuid("Target");

        await storeSymbol(storage, "Parent", parentGuid);
        await storeSymbol(storage, "Parent.Child", childGuid);
        await storeSymbol(storage, "Target", targetGuid);

        // Parent references Target, and Parent.Child also references Target
        // Same target, same relationship type, different source
        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = parentGuid, TargetSymbolId = targetGuid, RelationshipType = "References" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = childGuid, TargetSymbolId = targetGuid, RelationshipType = "References" },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.FindRelatedAsync("Parent", "all");

        // Two different relationship records to the same target — both should appear
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task FindRelatedAsync_ByNameFallback_ResolvesShortName()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        // Simulate a real scenario: FullName = "MyApp.Service" but Name = "Service"
        var classGuid = makeGuid("MyApp.Service");
        await storage.StoreSymbolsAsync([new SymbolRecord
        {
            Id = classGuid, Name = "Service", Kind = "Class",
            FilePath = "/src/Service.cs", FullName = "MyApp.Service",
            LineStart = 1, LineEnd = 10
        }]);

        var loggerGuid = makeGuid("MyApp.Logger");
        await storage.StoreSymbolsAsync([new SymbolRecord
        {
            Id = loggerGuid, Name = "Logger", Kind = "Class",
            FilePath = "/src/Logger.cs", FullName = "MyApp.Logger",
            LineStart = 1, LineEnd = 10
        }]);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = classGuid, TargetSymbolId = loggerGuid, RelationshipType = "References"
            },
        ]);

        var graph = createGraphService(storage);

        // Short name "Service" should fail FullName match, fall back to Name match
        var result = await graph.FindRelatedAsync("Service", "all");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SymbolName, Is.EqualTo("Logger"));
    }

    [Test]
    public async Task TraceAsync_ByNameFallback_ResolvesShortName()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var classGuid = makeGuid("MyApp.Service");
        await storage.StoreSymbolsAsync([new SymbolRecord
        {
            Id = classGuid, Name = "Service", Kind = "Class",
            FilePath = "/src/Service.cs", FullName = "MyApp.Service",
            LineStart = 1, LineEnd = 10
        }]);

        var loggerGuid = makeGuid("MyApp.Logger");
        await storage.StoreSymbolsAsync([new SymbolRecord
        {
            Id = loggerGuid, Name = "Logger", Kind = "Class",
            FilePath = "/src/Logger.cs", FullName = "MyApp.Logger",
            LineStart = 1, LineEnd = 10
        }]);

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = classGuid, TargetSymbolId = loggerGuid, RelationshipType = "References"
            },
        ]);

        var graph = createGraphService(storage);

        // Short name "Service" should fail FullName match, fall back to Name match
        var result = await graph.TraceAsync("Service", "upstream", 1);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SymbolName, Is.EqualTo("Logger"));
    }

    [Test]
    public async Task TraceAsync_IncludesChildMethodRelationships()
    {
        (var repoRoot, var dbPath) = GetTempDbPath();
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var serviceGuid = makeGuid("Service");
        var loggerGuid = makeGuid("Logger");
        var execMethodGuid = makeGuid("Service.Execute");

        await storeSymbol(storage, "Service", serviceGuid);
        await storeSymbol(storage, "Logger", loggerGuid);
        await storeSymbol(storage, "Service.Execute", execMethodGuid);

        // Service.Execute calls Logger (upstream from Execute's perspective)
        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "r1", SourceSymbolId = execMethodGuid, TargetSymbolId = loggerGuid, RelationshipType = "Calls"
            },
        ]);

        var graph = createGraphService(storage);
        var result = await graph.TraceAsync("Service", "upstream", 1);

        // Should find Logger through child method propagation
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SymbolName, Is.EqualTo("Logger"));
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
