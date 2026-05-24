using CodeMemory.Mcp.SqlQuery;
using CodeMemory.Storage;
using Memori.Embeddings;
using Memori.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq.Expressions;

namespace CodeMemory.Tests.Services.Query;

public sealed class VirtualTableTests
{
    static (InMemoryVectorStore Store, CollectionRegistry Registry, SqlQueryService Service, VirtualTableMaterializer Materializer) createServices()
    {
        var store = new InMemoryVectorStore();
        var registry = new CollectionRegistry();
        var embeddingGenerator = new NgramEmbeddingGenerator();
        var logger = NullLogger<SqlQueryService>.Instance;
        var matLogger = NullLogger<VirtualTableMaterializer>.Instance;
        var service = new SqlQueryService(registry, embeddingGenerator, logger);
        var materializer = new VirtualTableMaterializer(registry, matLogger);
        return (store, registry, service, materializer);
    }

    static async Task seedTestDataAsync(InMemoryVectorStore store)
    {
        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:MyClass", Name = "MyClass", Kind = "Class", FilePath = "/src/MyClass.cs", FullName = "MyClass", LineStart = 1, LineEnd = 100, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:MyMethod", Name = "MyMethod", Kind = "Method", FilePath = "/src/MyClass.cs", FullName = "MyClass.MyMethod", LineStart = 10, LineEnd = 30, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Helper", Name = "Helper", Kind = "Class", FilePath = "/src/Helper.cs", FullName = "Helper", LineStart = 1, LineEnd = 50, Modifiers = "internal" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:IOld", Name = "IOld", Kind = "Interface", FilePath = "/src/IOld.cs", FullName = "IOld", LineStart = 1, LineEnd = 10, Modifiers = "public" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call1", SourceSymbolId = "s:IOld", TargetSymbolId = "s:MyClass", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call2", SourceSymbolId = "s:Helper", TargetSymbolId = "s:MyClass", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call3", SourceSymbolId = "s:IOld", TargetSymbolId = "s:Helper", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call4", SourceSymbolId = "s:MyMethod", TargetSymbolId = "s:Helper", RelationshipType = "Calls" });
    }

    [Test]
    public async Task VirtualTableMaterializer_Materializes_RelationshipWithNames()
    {
        var (store, registry, service, materializer) = createServices();
        await seedTestDataAsync(store);

        var symColl = store.GetCollection<string, SymbolRecord>("symbols");
        var symbols = new List<SymbolRecord>();
        await foreach (var sym in symColl.GetAsync((Expression<Func<SymbolRecord, bool>>)(_ => true), int.MaxValue))
            symbols.Add(sym!);
        Assert.That(symbols.Count, Is.EqualTo(4), "Expected 4 symbols");

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var relWithNamesColl = store.GetCollection<string, RelationshipWithNamesRecord>("relWithNames");
        var relWithNamesDirect = new List<RelationshipWithNamesRecord>();
        await foreach (var r in relWithNamesColl.GetAsync((Expression<Func<RelationshipWithNamesRecord, bool>>)(_ => true), int.MaxValue))
            relWithNamesDirect.Add(r!);
        Assert.That(relWithNamesDirect.Count, Is.EqualTo(4), $"Expected 4 RelationshipWithNames, got {relWithNamesDirect.Count}");

        var entry = registry.GetEntry("RelationshipWithNames");
        Assert.That(entry, Is.Not.Null, "Registry should have RelationshipWithNames entry");
        Assert.That(entry!.CollectionName, Is.EqualTo("relWithNames"));

        var result = await service.ExecuteAsync(store,
            "SELECT TargetName, SourceName, RelationshipType FROM RelationshipWithNames ORDER BY TargetName, SourceName");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.RowCount, Is.EqualTo(4), $"Expected 4 rows, got {result.RowCount}. Error: {result.Error}");

        var rows = result.Rows!.OrderBy(r => $"{r["TargetName"]}:{r["SourceName"]}").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows[0]["TargetName"], Is.EqualTo("Helper"));
            Assert.That(rows[0]["SourceName"], Is.EqualTo("IOld"));

            Assert.That(rows[1]["TargetName"], Is.EqualTo("Helper"));
            Assert.That(rows[1]["SourceName"], Is.EqualTo("MyMethod"));

            Assert.That(rows[2]["TargetName"], Is.EqualTo("MyClass"));
            Assert.That(rows[2]["SourceName"], Is.EqualTo("Helper"));

            Assert.That(rows[3]["TargetName"], Is.EqualTo("MyClass"));
            Assert.That(rows[3]["SourceName"], Is.EqualTo("IOld"));
        });
    }

    [Test]
    public async Task VirtualTableMaterializer_Materializes_SymbolReferenceStats()
    {
        var (store, _, service, materializer) = createServices();
        await seedTestDataAsync(store);

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, IncomingReferences, OutgoingReferences, IncomingCalls FROM SymbolReferenceStats ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(4));

        var rows = result.Rows!.OrderBy(r => (string)r["Name"]!).ToList();

        var helperRow = rows.Single(r => (string)r["Name"]! == "Helper");
        var iOldRow = rows.Single(r => (string)r["Name"]! == "IOld");
        var myClassRow = rows.Single(r => (string)r["Name"]! == "MyClass");
        var myMethodRow = rows.Single(r => (string)r["Name"]! == "MyMethod");

        Assert.Multiple(() =>
        {
            Assert.That((long)myClassRow["IncomingReferences"]!, Is.EqualTo(2));
            Assert.That((long)myClassRow["OutgoingReferences"]!, Is.EqualTo(0));

            Assert.That((long)helperRow["IncomingReferences"]!, Is.EqualTo(2));
            Assert.That((long)helperRow["OutgoingReferences"]!, Is.EqualTo(1));
            Assert.That((long)helperRow["IncomingCalls"]!, Is.EqualTo(1));

            Assert.That((long)iOldRow["IncomingReferences"]!, Is.EqualTo(0));
            Assert.That((long)iOldRow["OutgoingReferences"]!, Is.EqualTo(2));

            Assert.That((long)myMethodRow["IncomingReferences"]!, Is.EqualTo(0));
            Assert.That((long)myMethodRow["OutgoingReferences"]!, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RelationshipWithNames_QueriesByKind_SimplerThanJoin()
    {
        var (store, _, service, materializer) = createServices();
        await seedTestDataAsync(store);
        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result = await service.ExecuteAsync(store,
            "SELECT TargetName, COUNT(*) AS refCount FROM RelationshipWithNames " +
            "WHERE TargetKind = 'Class' GROUP BY TargetName ORDER BY refCount DESC, TargetName");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));

        var rows = result.Rows!.OrderByDescending(r => (long)r["refCount"]!).ThenBy(r => (string)r["TargetName"]!).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows[0]["TargetName"], Is.EqualTo("Helper"));
            Assert.That((long)rows[0]["refCount"]!, Is.EqualTo(2));

            Assert.That(rows[1]["TargetName"], Is.EqualTo("MyClass"));
            Assert.That((long)rows[1]["refCount"]!, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task SymbolReferenceStats_MostReferencedClass_SimplestQuery()
    {
        var (store, _, service, materializer) = createServices();
        await seedTestDataAsync(store);
        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, IncomingReferences FROM SymbolReferenceStats " +
            "WHERE Kind = 'Class' ORDER BY IncomingReferences DESC, Name LIMIT 10");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));

        var rows = result.Rows!.OrderByDescending(r => (long)r["IncomingReferences"]!).ThenBy(r => (string)r["Name"]!).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows[0]["Name"], Is.EqualTo("Helper"));
            Assert.That((long)rows[0]["IncomingReferences"]!, Is.EqualTo(2));

            Assert.That(rows[1]["Name"], Is.EqualTo("MyClass"));
            Assert.That((long)rows[1]["IncomingReferences"]!, Is.EqualTo(2));
        });
    }

    #region Edge Case Tests

    [Test]
    public async Task EdgeCase_EmptyRepository_NoSymbolsNoRelationships()
    {
        var (store, registry, service, materializer) = createServices();

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var relWithNamesEntry = registry.GetEntry("RelationshipWithNames");
        var refStatsEntry = registry.GetEntry("SymbolReferenceStats");
        Assert.That(relWithNamesEntry, Is.Not.Null);
        Assert.That(refStatsEntry, Is.Not.Null);

        var relWithNamesColl = store.GetCollection<string, RelationshipWithNamesRecord>("relWithNames");
        var relWithNamesDirect = new List<RelationshipWithNamesRecord>();
        await foreach (var r in relWithNamesColl.GetAsync((Expression<Func<RelationshipWithNamesRecord, bool>>)(_ => true), int.MaxValue))
            relWithNamesDirect.Add(r!);
        Assert.That(relWithNamesDirect.Count, Is.EqualTo(0));

        var refStatsColl = store.GetCollection<string, SymbolReferenceStatsRecord>("refStats");
        var refStatsDirect = new List<SymbolReferenceStatsRecord>();
        await foreach (var r in refStatsColl.GetAsync((Expression<Func<SymbolReferenceStatsRecord, bool>>)(_ => true), int.MaxValue))
            refStatsDirect.Add(r!);
        Assert.That(refStatsDirect.Count, Is.EqualTo(0));

        var result = await service.ExecuteAsync(store, "SELECT * FROM SymbolReferenceStats");
        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(0));
    }

    [Test]
    public async Task EdgeCase_SymbolsOnly_NoRelationships()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:ClassA", Name = "ClassA", Kind = "Class", FilePath = "/src/A.cs", FullName = "ClassA", LineStart = 1, LineEnd = 10, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:ClassB", Name = "ClassB", Kind = "Class", FilePath = "/src/B.cs", FullName = "ClassB", LineStart = 1, LineEnd = 10, Modifiers = "public" });

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, IncomingReferences, OutgoingReferences, IncomingCalls, OutgoingCalls FROM SymbolReferenceStats ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));

        var rows = result.Rows!.OrderBy(r => (string)r["Name"]!).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows[0]["Name"], Is.EqualTo("ClassA"));
            Assert.That((long)rows[0]["IncomingReferences"]!, Is.EqualTo(0));
            Assert.That((long)rows[0]["OutgoingReferences"]!, Is.EqualTo(0));
            Assert.That((long)rows[0]["IncomingCalls"]!, Is.EqualTo(0));
            Assert.That((long)rows[0]["OutgoingCalls"]!, Is.EqualTo(0));

            Assert.That(rows[1]["Name"], Is.EqualTo("ClassB"));
            Assert.That((long)rows[1]["IncomingReferences"]!, Is.EqualTo(0));
            Assert.That((long)rows[1]["OutgoingReferences"]!, Is.EqualTo(0));
        });

        var relResult = await service.ExecuteAsync(store, "SELECT * FROM RelationshipWithNames");
        Assert.That(relResult.Success, Is.True);
        Assert.That(relResult.RowCount, Is.EqualTo(0));
    }

    [Test]
    public async Task EdgeCase_SelfReferencingSymbol()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Recursive", Name = "Recursive", Kind = "Class", FilePath = "/src/Recursive.cs", FullName = "Recursive", LineStart = 1, LineEnd = 100, Modifiers = "public" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:self", SourceSymbolId = "s:Recursive", TargetSymbolId = "s:Recursive", RelationshipType = "References" });

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var relResult = await service.ExecuteAsync(store,
            "SELECT SourceName, TargetName, RelationshipType FROM RelationshipWithNames");

        Assert.That(relResult.Success, Is.True);
        Assert.That(relResult.RowCount, Is.EqualTo(1));
        Assert.That(relResult.Rows![0]["SourceName"], Is.EqualTo("Recursive"));
        Assert.That(relResult.Rows[0]["TargetName"], Is.EqualTo("Recursive"));

        var statsResult = await service.ExecuteAsync(store,
            "SELECT Name, IncomingReferences, OutgoingReferences FROM SymbolReferenceStats");

        Assert.That(statsResult.Success, Is.True);
        Assert.That(statsResult.RowCount, Is.EqualTo(1));
        Assert.That(statsResult.Rows![0]["Name"], Is.EqualTo("Recursive"));
        Assert.That((long)statsResult.Rows[0]["IncomingReferences"]!, Is.EqualTo(1));
        Assert.That((long)statsResult.Rows[0]["OutgoingReferences"]!, Is.EqualTo(1));
    }

    [Test]
    public async Task EdgeCase_OrphanedRelationships_NonExistentSymbols()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Real", Name = "Real", Kind = "Class", FilePath = "/src/Real.cs", FullName = "Real", LineStart = 1, LineEnd = 10, Modifiers = "public" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:1", SourceSymbolId = "s:Real", TargetSymbolId = "s:NonExistent1", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:2", SourceSymbolId = "s:NonExistent2", TargetSymbolId = "s:Real", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:3", SourceSymbolId = "s:NonExistent3", TargetSymbolId = "s:NonExistent4", RelationshipType = "References" });

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var relResult = await service.ExecuteAsync(store,
            "SELECT SourceName, TargetName, SourceSymbolId, TargetSymbolId FROM RelationshipWithNames ORDER BY SourceSymbolId");

        Assert.That(relResult.Success, Is.True);
        Assert.That(relResult.RowCount, Is.EqualTo(3));

        var rows = relResult.Rows!.OrderBy(r => (string)r["SourceSymbolId"]!).ToList();

        Assert.Multiple(() =>
        {
            var rowRealToNonExistent = rows.Single(r => (string)r["SourceSymbolId"]! == "s:Real");
            Assert.That(rowRealToNonExistent["SourceName"], Is.EqualTo("Real"));
            Assert.That(rowRealToNonExistent["TargetName"], Is.EqualTo(""));

            var rowNonExistentToReal = rows.Single(r => (string)r["SourceSymbolId"]! == "s:NonExistent2");
            Assert.That(rowNonExistentToReal["SourceName"], Is.EqualTo(""));
            Assert.That(rowNonExistentToReal["TargetName"], Is.EqualTo("Real"));

            var rowBothNonExistent = rows.Single(r => (string)r["SourceSymbolId"]! == "s:NonExistent3");
            Assert.That(rowBothNonExistent["SourceName"], Is.EqualTo(""));
            Assert.That(rowBothNonExistent["TargetName"], Is.EqualTo(""));
        });

        var statsResult = await service.ExecuteAsync(store,
            "SELECT Name, IncomingReferences, OutgoingReferences FROM SymbolReferenceStats");

        Assert.That(statsResult.Success, Is.True);
        Assert.That(statsResult.RowCount, Is.EqualTo(1));
        Assert.That(statsResult.Rows![0]["Name"], Is.EqualTo("Real"));
        Assert.That((long)statsResult.Rows[0]["IncomingReferences"]!, Is.EqualTo(1));
        Assert.That((long)statsResult.Rows[0]["OutgoingReferences"]!, Is.EqualTo(1));
    }

    [Test]
    public async Task EdgeCase_AllRelationshipTypes()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Consumer", Name = "Consumer", Kind = "Class", FilePath = "/src/Consumer.cs", FullName = "Consumer", LineStart = 1, LineEnd = 100, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Dependency", Name = "Dependency", Kind = "Class", FilePath = "/src/Dependency.cs", FullName = "Dependency", LineStart = 1, LineEnd = 100, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Base", Name = "Base", Kind = "Class", FilePath = "/src/Base.cs", FullName = "Base", LineStart = 1, LineEnd = 50, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:ILogger", Name = "ILogger", Kind = "Interface", FilePath = "/src/ILogger.cs", FullName = "ILogger", LineStart = 1, LineEnd = 10, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:DependencyTests", Name = "DependencyTests", Kind = "Class", FilePath = "/tests/DependencyTests.cs", FullName = "DependencyTests", LineStart = 1, LineEnd = 100, Modifiers = "public" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call", SourceSymbolId = "s:Consumer", TargetSymbolId = "s:Dependency", RelationshipType = "Calls" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:ref", SourceSymbolId = "s:Consumer", TargetSymbolId = "s:Dependency", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:inherit", SourceSymbolId = "s:Consumer", TargetSymbolId = "s:Base", RelationshipType = "Inherits" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:impl", SourceSymbolId = "s:Consumer", TargetSymbolId = "s:ILogger", RelationshipType = "Implements" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:test", SourceSymbolId = "s:DependencyTests", TargetSymbolId = "s:Dependency", RelationshipType = "TestCoverage" });

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var relWithNamesResult = await service.ExecuteAsync(store,
            "SELECT SourceName, TargetName, RelationshipType FROM RelationshipWithNames ORDER BY SourceName, RelationshipType");

        Assert.That(relWithNamesResult.Success, Is.True);
        Assert.That(relWithNamesResult.RowCount, Is.EqualTo(5));

        var statsResult = await service.ExecuteAsync(store,
            "SELECT Name, IncomingReferences, OutgoingReferences, IncomingCalls, OutgoingCalls, " +
            "IncomingInherits, OutgoingInherits, IncomingImplements, OutgoingImplements, " +
            "IncomingTestCoverage, OutgoingTestCoverage, " +
            "IncomingReferencesNonCall, OutgoingReferencesNonCall " +
            "FROM SymbolReferenceStats ORDER BY Name");

        Assert.That(statsResult.Success, Is.True);
        Assert.That(statsResult.RowCount, Is.EqualTo(5));

        var rows = statsResult.Rows!.ToDictionary(r => (string)r["Name"]!, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            var consumer = rows["Consumer"];
            Assert.That((long)consumer["OutgoingReferences"]!, Is.EqualTo(4));
            Assert.That((long)consumer["OutgoingCalls"]!, Is.EqualTo(1));
            Assert.That((long)consumer["OutgoingInherits"]!, Is.EqualTo(1));
            Assert.That((long)consumer["OutgoingImplements"]!, Is.EqualTo(1));
            Assert.That((long)consumer["OutgoingReferencesNonCall"]!, Is.EqualTo(3));

            var dependency = rows["Dependency"];
            Assert.That((long)dependency["IncomingReferences"]!, Is.EqualTo(3));
            Assert.That((long)dependency["IncomingCalls"]!, Is.EqualTo(1));
            Assert.That((long)dependency["IncomingTestCoverage"]!, Is.EqualTo(1));
            Assert.That((long)dependency["IncomingReferencesNonCall"]!, Is.EqualTo(2));

            var baseClass = rows["Base"];
            Assert.That((long)baseClass["IncomingInherits"]!, Is.EqualTo(1));

            var logger = rows["ILogger"];
            Assert.That((long)logger["IncomingImplements"]!, Is.EqualTo(1));

            var tests = rows["DependencyTests"];
            Assert.That((long)tests["OutgoingTestCoverage"]!, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task EdgeCase_MultipleMaterializations_Idempotent()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:A", Name = "A", Kind = "Class", FilePath = "/src/A.cs", FullName = "A", LineStart = 1, LineEnd = 10, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:B", Name = "B", Kind = "Class", FilePath = "/src/B.cs", FullName = "B", LineStart = 1, LineEnd = 10, Modifiers = "public" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:1", SourceSymbolId = "s:A", TargetSymbolId = "s:B", RelationshipType = "References" });

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result1 = await service.ExecuteAsync(store, "SELECT Name, IncomingReferences FROM SymbolReferenceStats ORDER BY Name");
        Assert.That(result1.Success, Is.True);
        Assert.That(result1.RowCount, Is.EqualTo(2));

        await materializer.MaterializeAsync(store, CancellationToken.None);
        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result2 = await service.ExecuteAsync(store, "SELECT Name, IncomingReferences FROM SymbolReferenceStats ORDER BY Name");
        Assert.That(result2.Success, Is.True);
        Assert.That(result2.RowCount, Is.EqualTo(2));

        for (int i = 0; i < 2; i++)
        {
            Assert.That(result2.Rows![i]["Name"], Is.EqualTo(result1.Rows![i]["Name"]));
            Assert.That((long)result2.Rows[i]["IncomingReferences"]!, Is.EqualTo((long)result1.Rows[i]["IncomingReferences"]!));
        }
    }

    [Test]
    public async Task EdgeCase_ConcurrentMaterialization_SingleThreaded()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        for (int i = 0; i < 10; i++)
        {
            await sym.UpsertAsync(new SymbolRecord { Id = $"s:{i}", Name = $"Class{i}", Kind = "Class", FilePath = $"/src/{i}.cs", FullName = $"Class{i}", LineStart = 1, LineEnd = 10, Modifiers = "public" });
        }

        var task1 = materializer.MaterializeAsync(store, CancellationToken.None);
        var task2 = materializer.MaterializeAsync(store, CancellationToken.None);
        var task3 = materializer.MaterializeAsync(store, CancellationToken.None);

        await Task.WhenAll(task1, task2, task3);

        var result = await service.ExecuteAsync(store, "SELECT COUNT(*) AS cnt FROM SymbolReferenceStats");
        Assert.That(result.Success, Is.True);
        Assert.That((long)result.Rows![0]["cnt"]!, Is.EqualTo(10));
    }

    [Test]
    public async Task EdgeCase_RelationshipWithNames_AllColumnsMatch()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Source", Name = "SourceClass", Kind = "Class", FilePath = "/src/Source.cs", FullName = "Namespace.SourceClass", LineStart = 10, LineEnd = 50, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Target", Name = "TargetMethod", Kind = "Method", FilePath = "/src/Target.cs", FullName = "Namespace.TargetMethod", LineStart = 20, LineEnd = 30, Modifiers = "private" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:test", SourceSymbolId = "s:Source", TargetSymbolId = "s:Target", RelationshipType = "Calls" });

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM RelationshipWithNames");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));

        var row = result.Rows![0];

        Assert.Multiple(() =>
        {
            Assert.That(row["Id"], Is.EqualTo("r:test"));
            Assert.That(row["SourceSymbolId"], Is.EqualTo("s:Source"));
            Assert.That(row["SourceName"], Is.EqualTo("SourceClass"));
            Assert.That(row["SourceKind"], Is.EqualTo("Class"));
            Assert.That(row["SourceFullName"], Is.EqualTo("Namespace.SourceClass"));
            Assert.That(row["SourceFilePath"], Is.EqualTo("/src/Source.cs"));
            Assert.That(row["TargetSymbolId"], Is.EqualTo("s:Target"));
            Assert.That(row["TargetName"], Is.EqualTo("TargetMethod"));
            Assert.That(row["TargetKind"], Is.EqualTo("Method"));
            Assert.That(row["TargetFullName"], Is.EqualTo("Namespace.TargetMethod"));
            Assert.That(row["TargetFilePath"], Is.EqualTo("/src/Target.cs"));
            Assert.That(row["RelationshipType"], Is.EqualTo("Calls"));
        });
    }

    [Test]
    public async Task EdgeCase_SymbolReferenceStats_AllColumnsMatch()
    {
        var (store, _, service, materializer) = createServices();

        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Test", Name = "TestSymbol", Kind = "Method", FilePath = "/src/Test.cs", FullName = "TestSymbol", LineStart = 1, LineEnd = 10, Modifiers = "public" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:1", SourceSymbolId = "s:Other", TargetSymbolId = "s:Test", RelationshipType = "Calls" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:2", SourceSymbolId = "s:Other", TargetSymbolId = "s:Test", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:3", SourceSymbolId = "s:Other", TargetSymbolId = "s:Test", RelationshipType = "TestCoverage" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:4", SourceSymbolId = "s:Test", TargetSymbolId = "s:Another", RelationshipType = "Inherits" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:5", SourceSymbolId = "s:Test", TargetSymbolId = "s:Another", RelationshipType = "Implements" });

        await materializer.MaterializeAsync(store, CancellationToken.None);

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM SymbolReferenceStats WHERE Name = 'TestSymbol'");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));

        var row = result.Rows![0];

        Assert.Multiple(() =>
        {
            Assert.That(row["SymbolId"], Is.EqualTo("s:Test"));
            Assert.That(row["Name"], Is.EqualTo("TestSymbol"));
            Assert.That(row["Kind"], Is.EqualTo("Method"));
            Assert.That(row["FullName"], Is.EqualTo("TestSymbol"));
            Assert.That(row["FilePath"], Is.EqualTo("/src/Test.cs"));
            Assert.That((long)row["IncomingReferences"]!, Is.EqualTo(3));
            Assert.That((long)row["OutgoingReferences"]!, Is.EqualTo(2));
            Assert.That((long)row["IncomingCalls"]!, Is.EqualTo(1));
            Assert.That((long)row["OutgoingCalls"]!, Is.EqualTo(0));
            Assert.That((long)row["IncomingReferencesNonCall"]!, Is.EqualTo(2));
            Assert.That((long)row["OutgoingReferencesNonCall"]!, Is.EqualTo(2));
            Assert.That((long)row["IncomingInherits"]!, Is.EqualTo(0));
            Assert.That((long)row["OutgoingInherits"]!, Is.EqualTo(1));
            Assert.That((long)row["IncomingImplements"]!, Is.EqualTo(0));
            Assert.That((long)row["OutgoingImplements"]!, Is.EqualTo(1));
            Assert.That((long)row["IncomingTestCoverage"]!, Is.EqualTo(1));
            Assert.That((long)row["OutgoingTestCoverage"]!, Is.EqualTo(0));
        });
    }

    #endregion
}
