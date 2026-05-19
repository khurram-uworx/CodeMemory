using CodeMemory.Mcp.SqlQuery;
using CodeMemory.Storage;
using Memori.Embeddings;
using Memori.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Query;

public sealed class SqlQueryServiceTests
{
    static (InMemoryVectorStore Store, CollectionRegistry Registry, SqlQueryService Service) createServices()
    {
        var store = new InMemoryVectorStore();
        var registry = new CollectionRegistry();
        var embeddingGenerator = new NgramEmbeddingGenerator();
        var logger = NullLogger<SqlQueryService>.Instance;
        var service = new SqlQueryService(registry, embeddingGenerator, logger);
        return (store, registry, service);
    }

    static async Task seedSymbolsAsync(InMemoryVectorStore store)
    {
        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:MyClass",
            Name = "MyClass",
            Kind = "Class",
            FilePath = "/src/MyClass.cs",
            FullName = "MyClass",
            LineStart = 1,
            LineEnd = 50,
            Modifiers = "public"
        });
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:MyMethod",
            Name = "MyMethod",
            Kind = "Method",
            FilePath = "/src/MyClass.cs",
            FullName = "MyClass.MyMethod",
            LineStart = 10,
            LineEnd = 20,
            Modifiers = "public"
        });
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:Helper",
            Name = "Helper",
            Kind = "Class",
            FilePath = "/src/Helper.cs",
            FullName = "Helper",
            LineStart = 1,
            LineEnd = 30,
            Modifiers = "internal"
        });
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:IOld",
            Name = "IOld",
            Kind = "Interface",
            FilePath = "/src/IOld.cs",
            FullName = "IOld",
            LineStart = 1,
            LineEnd = 10,
            Modifiers = "public"
        });
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:_private",
            Name = "_private",
            Kind = "Field",
            FilePath = "/src/MyClass.cs",
            FullName = "MyClass._private",
            LineStart = 5,
            LineEnd = 5,
            Modifiers = "private"
        });
    }

    static async Task seedChunksAsync(InMemoryVectorStore store)
    {
        var coll = store.GetCollection<string, ChunkRecord>("chunks");
        var gen = new NgramEmbeddingGenerator();
        var embedding = await gen.GenerateAsync(["authentication service"], cancellationToken: default);
        await coll.UpsertAsync(new ChunkRecord
        {
            Id = "c:auth",
            SymbolId = "s:AuthService",
            FilePath = "/src/Auth.cs",
            Content = "public class AuthService handles authentication",
            Language = "CSharp",
            LineStart = 1,
            LineEnd = 5,
            Embedding = embedding[0].Vector
        });
        var embedding2 = await gen.GenerateAsync(["database connection"], cancellationToken: default);
        await coll.UpsertAsync(new ChunkRecord
        {
            Id = "c:db",
            SymbolId = "s:DbService",
            FilePath = "/src/Db.cs",
            Content = "public class DbService manages database connection",
            Language = "CSharp",
            LineStart = 1,
            LineEnd = 5,
            Embedding = embedding2[0].Vector
        });
    }

    [Test]
    public async Task Select_WithKindFilter_ReturnsMatchingRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows!.Select(r => r["Name"]), Is.EquivalentTo(["MyClass", "Helper"]));
    }

    [Test]
    public async Task Select_WithAndLikeFilter_ReturnsMatchingRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Method' AND FilePath LIKE '%MyClass%'");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Rows![0]["Name"], Is.EqualTo("MyMethod"));
    }

    [Test]
    public async Task Select_WithInFilterAndOrderBy_ReturnsSortedRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, FilePath FROM SymbolRecord WHERE Kind IN ('Class', 'Interface') ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        Assert.That(result.Rows!.Select(r => r["Name"]),
            Is.EqualTo(["Helper", "IOld", "MyClass"]));
    }

    [Test]
    public async Task Select_WithLikeOnModifiersNullableColumn_HandlesNull()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord WHERE Modifiers LIKE '%public%' ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows!.Select(r => r["Name"]),
            Is.EquivalentTo(["IOld", "MyClass", "MyMethod"]));
    }

    [Test]
    public async Task VectorSearch_ReturnsScoredResults()
    {
        var (store, registry, service) = createServices();
        await seedChunksAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, Content FROM ChunkRecord " +
            "WHERE Content LIKE '%auth%' ORDER BY Similarity DESC LIMIT 5");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(result.Rows![0]["FilePath"], Is.EqualTo("/src/Auth.cs"));
        Assert.That(result.Rows![0], Contains.Key("__score"));
    }

    [Test]
    public async Task UnknownTable_ReturnsError()
    {
        var (store, registry, service) = createServices();

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM NonExistentTable");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Unknown table"));
    }

    [Test]
    public async Task NonSelectStatement_ReturnsError()
    {
        var (store, registry, service) = createServices();

        var result = await service.ExecuteAsync(store,
            "INSERT INTO SymbolRecord VALUES (1)");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Only SELECT"));
    }

    [Test]
    public async Task EmptyWhere_ReturnsAllRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Id FROM SymbolRecord LIMIT 3");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
    }

    [Test]
    public async Task Select_WithNotEq_ReturnsMatchingRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord WHERE Kind <> 'Class'");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows!.Select(r => r["Name"]),
            Is.EquivalentTo(["MyMethod", "IOld", "_private"]));
    }

    [Test]
    public async Task Select_WithIsNullOnModifiers_ReturnsRows()
    {
        var (store, registry, service) = createServices();

        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:NoMod",
            Name = "NoMod",
            Kind = "Class",
            FilePath = "/src/NoMod.cs",
            FullName = "NoMod",
            LineStart = 1,
            LineEnd = 10
        });

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord WHERE Modifiers IS NULL");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Rows![0]["Name"], Is.EqualTo("NoMod"));
    }

    [Test]
    public async Task GroupBy_CountStarWithWhereIn_ReturnsGroupedCounts()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS SymbolCount FROM SymbolRecord " +
            "WHERE Kind IN ('Class', 'Interface', 'Struct', 'Enum', 'Record') " +
            "GROUP BY FilePath ORDER BY SymbolCount DESC LIMIT 20");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        Assert.That(result.Columns, Is.EquivalentTo(["FilePath", "SymbolCount"]));

        var byPath = result.Rows!.ToDictionary(r => (string)r["FilePath"]!);
        Assert.That((long)byPath["/src/MyClass.cs"]["SymbolCount"], Is.EqualTo(1));
        Assert.That((long)byPath["/src/Helper.cs"]["SymbolCount"], Is.EqualTo(1));
        Assert.That((long)byPath["/src/IOld.cs"]["SymbolCount"], Is.EqualTo(1));
    }

    [Test]
    public async Task GroupBy_CountStarWithMethodFilter_ReturnsGroupedCounts()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS MethodCount FROM SymbolRecord " +
            "WHERE Kind IN ('Method', 'Function', 'Constructor') " +
            "GROUP BY FilePath ORDER BY MethodCount DESC LIMIT 20");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Columns, Is.EquivalentTo(["FilePath", "MethodCount"]));
        Assert.That((long)result.Rows![0]["MethodCount"], Is.EqualTo(1));
        Assert.That(result.Rows[0]["FilePath"], Is.EqualTo("/src/MyClass.cs"));
    }

    [Test]
    public async Task GroupBy_CountStarAllRows_ReturnsGroupedCounts()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS TotalSymbols FROM SymbolRecord " +
            "GROUP BY FilePath ORDER BY TotalSymbols DESC LIMIT 20");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));

        var byPath = result.Rows!.ToDictionary(r => (string)r["FilePath"]!);
        Assert.That((long)byPath["/src/MyClass.cs"]["TotalSymbols"], Is.EqualTo(3));
        Assert.That((long)byPath["/src/Helper.cs"]["TotalSymbols"], Is.EqualTo(1));
        Assert.That((long)byPath["/src/IOld.cs"]["TotalSymbols"], Is.EqualTo(1));
    }

    [Test]
    public async Task GroupBy_CountStarShortAlias_ReturnsGroupedCounts()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord " +
            "GROUP BY FilePath ORDER BY cnt DESC LIMIT 20");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        Assert.That(result.Columns, Is.EquivalentTo(["FilePath", "cnt"]));

        var byPath = result.Rows!.ToDictionary(r => (string)r["FilePath"]!);
        Assert.That((long)byPath["/src/MyClass.cs"]["cnt"], Is.EqualTo(3));
        Assert.That((long)byPath["/src/Helper.cs"]["cnt"], Is.EqualTo(1));
        Assert.That((long)byPath["/src/IOld.cs"]["cnt"], Is.EqualTo(1));
    }

    // ----- Feature: explicit column projection (non-GROUP BY) -----

    [Test]
    public async Task Select_ExplicitProjection_ReturnsOnlySelectedColumns()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, Kind FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Columns, Is.EquivalentTo(["Name", "Kind"]));
        foreach (var row in result.Rows!)
        {
            Assert.That(row.ContainsKey("Id"), Is.False);
            Assert.That(row.ContainsKey("FilePath"), Is.False);
        }
    }

    // ----- Feature: more aggregate functions -----

    [Test]
    public async Task Aggregate_SumAvgMinMax_OnNumericColumn()
    {
        var (store, registry, service) = createServices();
        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        for (int i = 1; i <= 5; i++)
        {
            await coll.UpsertAsync(new SymbolRecord
            {
                Id = $"s:item{i}",
                Name = $"Item{i}",
                Kind = "Data",
                FilePath = "/src/data.cs",
                FullName = $"Item{i}",
                LineStart = i * 10,
                LineEnd = i * 10 + 5
            });
        }

        var result = await service.ExecuteAsync(store,
            "SELECT Kind, COUNT(*) AS cnt, SUM(LineStart) AS total, " +
            "AVG(LineStart) AS avg, MIN(LineStart) AS min, MAX(LineStart) AS max " +
            "FROM SymbolRecord WHERE Kind = 'Data' GROUP BY Kind");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        var row = result.Rows![0];
        Assert.That((long)row["cnt"], Is.EqualTo(5));
        Assert.That(Convert.ToDouble(row["total"]), Is.EqualTo(150.0));
        Assert.That(Convert.ToDouble(row["avg"]), Is.EqualTo(30.0));
        Assert.That(Convert.ToDouble(row["min"]), Is.EqualTo(10.0));
        Assert.That(Convert.ToDouble(row["max"]), Is.EqualTo(50.0));
    }

    [Test]
    public async Task Aggregate_CountColumn_ExcludesNulls()
    {
        var (store, registry, service) = createServices();
        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:withMod",
            Name = "WithMod",
            Kind = "Class",
            FilePath = "/src/a.cs",
            FullName = "WithMod",
            Modifiers = "public"
        });
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:noMod",
            Name = "NoMod",
            Kind = "Class",
            FilePath = "/src/b.cs",
            FullName = "NoMod"
        });

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(Modifiers) AS cnt FROM SymbolRecord " +
            "WHERE Kind = 'Class' GROUP BY FilePath ORDER BY FilePath");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        var byPath = result.Rows!.ToDictionary(r => (string)r["FilePath"]!);
        Assert.That((long)byPath["/src/a.cs"]["cnt"], Is.EqualTo(1));
        Assert.That((long)byPath["/src/b.cs"]["cnt"], Is.EqualTo(0));
    }

    // ----- Feature: HAVING -----

    [Test]
    public async Task GroupBy_Having_CountGtOne_FiltersGroups()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord " +
            "GROUP BY FilePath HAVING COUNT(*) > 1");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That((string)result.Rows![0]["FilePath"], Is.EqualTo("/src/MyClass.cs"));
        Assert.That((long)result.Rows[0]["cnt"], Is.EqualTo(3));
    }

    [Test]
    public async Task GroupBy_Having_AliasGtOne_FiltersGroups()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord " +
            "GROUP BY FilePath HAVING cnt > 1");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That((string)result.Rows![0]["FilePath"], Is.EqualTo("/src/MyClass.cs"));
    }

    // ----- Feature: DISTINCT -----

    [Test]
    public async Task Select_DistinctKind_ReturnsUniqueValues()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT DISTINCT Kind FROM SymbolRecord ORDER BY Kind");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Columns, Is.EquivalentTo(["Kind"]));
        Assert.That(result.Rows!.Select(r => r["Kind"]),
            Is.EqualTo(["Class", "Field", "Interface", "Method"]));
        Assert.That(result.RowCount, Is.EqualTo(4));
    }

    [Test]
    public async Task Select_DistinctMultiColumn_ReturnsUniqueCombinations()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT DISTINCT Kind, FilePath FROM SymbolRecord ORDER BY Kind, FilePath");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        Assert.That(result.Columns, Is.EquivalentTo(["Kind", "FilePath"]));
    }

    // ----- Feature: ORDER BY numeric position -----

    [Test]
    public async Task Select_OrderByNumericPosition_ReturnsSorted()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, Kind FROM SymbolRecord " +
            "WHERE Kind IN ('Class', 'Interface') ORDER BY 1");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows!.Select(r => (string)r["Name"]!),
            Is.EqualTo(["Helper", "IOld", "MyClass"]));
    }

    [Test]
    public async Task GroupBy_OrderByNumericPosition_ReturnsSorted()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord " +
            "GROUP BY FilePath ORDER BY 2 DESC");

        Assert.That(result.Success, Is.True);
        var rows = result.Rows!;
        Assert.That((long)rows[0]["cnt"], Is.EqualTo(3));
        Assert.That((string)rows[0]["FilePath"], Is.EqualTo("/src/MyClass.cs"));
    }

    // ----- Feature: multi-column ORDER BY -----

    [Test]
    public async Task OrderBy_MultiColumnKindThenName_ReturnsCorrectOrder()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, Kind FROM SymbolRecord ORDER BY Kind, Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        var rows = result.Rows!;
        Assert.That((string)rows[0]["Name"]!, Is.EqualTo("Helper"));
        Assert.That((string)rows[0]["Kind"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[1]["Name"]!, Is.EqualTo("MyClass"));
        Assert.That((string)rows[1]["Kind"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[2]["Name"]!, Is.EqualTo("_private"));
        Assert.That((string)rows[2]["Kind"]!, Is.EqualTo("Field"));
        Assert.That((string)rows[3]["Name"]!, Is.EqualTo("IOld"));
        Assert.That((string)rows[3]["Kind"]!, Is.EqualTo("Interface"));
        Assert.That((string)rows[4]["Name"]!, Is.EqualTo("MyMethod"));
        Assert.That((string)rows[4]["Kind"]!, Is.EqualTo("Method"));
    }

    [Test]
    public async Task OrderBy_MultiColumnDescAsc_RespectsPerColumnDirection()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, Kind FROM SymbolRecord ORDER BY Kind DESC, Name ASC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        var rows = result.Rows!;
        Assert.That((string)rows[0]["Kind"]!, Is.EqualTo("Method"));
        Assert.That((string)rows[1]["Kind"]!, Is.EqualTo("Interface"));
        Assert.That((string)rows[2]["Kind"]!, Is.EqualTo("Field"));
        Assert.That((string)rows[3]["Kind"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[4]["Kind"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[3]["Name"]!, Is.EqualTo("Helper"));
        Assert.That((string)rows[4]["Name"]!, Is.EqualTo("MyClass"));
    }

    [Test]
    public async Task OrderBy_MultiColumnWithAlias_WorksCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        // ORDER BY k ASC, n ASC
        // Seed data sorts: Field < Class < Interface < Method alphabetically
        // Within Class: Helper < MyClass
        var result = await service.ExecuteAsync(store,
            "SELECT Kind AS k, Name AS n FROM SymbolRecord ORDER BY k, n");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        var rows = result.Rows!;
        Assert.That((string)rows[0]["k"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[0]["n"]!, Is.EqualTo("Helper"));
        Assert.That((string)rows[1]["k"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[1]["n"]!, Is.EqualTo("MyClass"));
        Assert.That((string)rows[2]["k"]!, Is.EqualTo("Field"));
        Assert.That((string)rows[2]["n"]!, Is.EqualTo("_private"));
        Assert.That((string)rows[3]["k"]!, Is.EqualTo("Interface"));
        Assert.That((string)rows[3]["n"]!, Is.EqualTo("IOld"));
        Assert.That((string)rows[4]["k"]!, Is.EqualTo("Method"));
        Assert.That((string)rows[4]["n"]!, Is.EqualTo("MyMethod"));
    }

    [Test]
    public async Task OrderBy_MultiColumnWithNumericPosition_WorksCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Kind, Name FROM SymbolRecord ORDER BY 1, 2");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        var rows = result.Rows!;
        Assert.That((string)rows[0]["Kind"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[0]["Name"]!, Is.EqualTo("Helper"));
    }

    // ----- Feature: arithmetic expressions in SELECT -----

    [Test]
    public async Task Select_ArithmeticSubtractWithAlias_ComputesCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, LineEnd - LineStart AS Length FROM SymbolRecord ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));

        var byName = result.Rows!.ToDictionary(r => (string)r["Name"]!);
        Assert.That(Convert.ToDouble(byName["MyClass"]["Length"]), Is.EqualTo(49.0));
        Assert.That(Convert.ToDouble(byName["MyMethod"]["Length"]), Is.EqualTo(10.0));
        Assert.That(Convert.ToDouble(byName["Helper"]["Length"]), Is.EqualTo(29.0));
        Assert.That(Convert.ToDouble(byName["IOld"]["Length"]), Is.EqualTo(9.0));
        Assert.That(Convert.ToDouble(byName["_private"]["Length"]), Is.EqualTo(0.0));
    }

    [Test]
    public async Task Select_ArithmeticMultipleOps_ComputesCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, LineEnd + LineStart AS total, (LineEnd - LineStart) * 2 AS doubled FROM SymbolRecord WHERE Name = 'MyClass' LIMIT 1");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        var row = result.Rows![0];
        Assert.That(row, Contains.Key("total"));
        Assert.That(row, Contains.Key("doubled"));
        Assert.That(Convert.ToDouble(row["total"]), Is.EqualTo(51.0));
        Assert.That(Convert.ToDouble(row["doubled"]), Is.EqualTo(98.0));
    }

    [Test]
    public async Task Select_ArithmeticUnnamed_GeneratesColumnName()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, LineEnd - LineStart FROM SymbolRecord WHERE Name = 'Helper'");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Columns, Has.Member("LineEnd - LineStart"));
        Assert.That(Convert.ToDouble(result.Rows![0]["LineEnd - LineStart"]), Is.EqualTo(29.0));
    }

    [Test]
    public async Task Select_ArithmeticWithNull_ReturnsNull()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:nullMod",
            Name = "NullMod",
            Kind = "Class",
            FilePath = "/src/NullMod.cs",
            FullName = "NullMod",
            LineStart = 10,
            LineEnd = 20
        });

        var result = await service.ExecuteAsync(store,
            "SELECT Name, LineEnd - LineStart AS Len FROM SymbolRecord WHERE Name = 'NullMod'");

        Assert.That(result.Success, Is.True);
        Assert.That(Convert.ToDouble(result.Rows![0]["Len"]), Is.EqualTo(10.0));
    }

    [Test]
    public async Task Select_ArithmeticDivision_ComputesCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, LineStart / 10 AS decile FROM SymbolRecord WHERE Name = 'MyMethod' LIMIT 1");

        Assert.That(result.Success, Is.True);
        Assert.That(Convert.ToDouble(result.Rows![0]["decile"]), Is.EqualTo(1.0));
    }

    [Test]
    public async Task Select_ArithmeticMultiplication_ComputesCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, LineStart * 2 AS doubled FROM SymbolRecord WHERE Name = 'Helper' LIMIT 1");

        Assert.That(result.Success, Is.True);
        Assert.That(Convert.ToDouble(result.Rows![0]["doubled"]), Is.EqualTo(2.0));
    }

    // ----- Feature: ORDER BY with arithmetic expression alias -----

    [Test]
    public async Task OrderBy_ArithmeticAlias_SortsCorrectlyInGroupBy()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord " +
            "GROUP BY FilePath ORDER BY cnt DESC, FilePath ASC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        var rows = result.Rows!;
        Assert.That((long)rows[0]["cnt"], Is.EqualTo(3));
        Assert.That((string)rows[0]["FilePath"], Is.EqualTo("/src/MyClass.cs"));
        Assert.That((long)rows[1]["cnt"], Is.EqualTo(1));
    }

    [Test]
    public async Task OrderBy_MultiColumnGroupBy_WorksWithAggregates()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Kind, COUNT(*) AS cnt FROM SymbolRecord " +
            "GROUP BY Kind ORDER BY cnt DESC, Kind ASC");

        Assert.That(result.Success, Is.True);
        var rows = result.Rows!;
        Assert.That((long)rows[0]["cnt"], Is.EqualTo(2));
        Assert.That((string)rows[0]["Kind"], Is.EqualTo("Class"));
        Assert.That((long)rows[1]["cnt"], Is.EqualTo(1));
    }

    [Test]
    public async Task DistinctMultiColumn_OrderByKindName_ReturnsCorrectOrder()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT DISTINCT Kind, FilePath FROM SymbolRecord ORDER BY Kind, FilePath");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        var rows = result.Rows!;
        Assert.That((string)rows[0]["Kind"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[1]["Kind"]!, Is.EqualTo("Class"));
        Assert.That((string)rows[2]["Kind"]!, Is.EqualTo("Field"));
        Assert.That((string)rows[3]["Kind"]!, Is.EqualTo("Interface"));
        Assert.That((string)rows[4]["Kind"]!, Is.EqualTo("Method"));
    }

    // ----- Feature: table alias in column references -----

    [Test]
    public async Task Select_WithTableAlias_WorksCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        // s.Name and s.Kind are CompoundIdentifier expressions (no alias),
        // so column keys become the expression strings "s.Name" and "s.Kind"
        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, s.Kind FROM SymbolRecord s WHERE s.Kind = 'Class' ORDER BY s.Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Columns, Does.Contain("s.Name"));
        Assert.That(result.Columns, Does.Contain("s.Kind"));
        Assert.That(result.Rows!.Select(r => r["s.Name"]), Is.EquivalentTo(["Helper", "MyClass"]));
    }

    // ----- Fix verification tests -----

    [Test]
    public async Task LimitZero_ReturnsEmptyResult()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM SymbolRecord LIMIT 0");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(0));
    }

    [Test]
    public async Task WhereWithParenthesizedExpression_ReturnsMatchingRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord WHERE (Kind = 'Class') ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows!.Select(r => r["Name"]), Is.EqualTo(["Helper", "MyClass"]));
    }

    [Test]
    public async Task WhereWithNestedAndOrParentheses_ReturnsCorrectRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord WHERE (Kind = 'Class' OR Kind = 'Interface') AND FilePath LIKE '%Helper%'");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Rows![0]["Name"], Is.EqualTo("Helper"));
    }

    [Test]
    public async Task OrderBy_ComputedAlias_SortsCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, LineEnd - LineStart AS Length FROM SymbolRecord ORDER BY Length");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));

        var lengths = result.Rows!.Select(r => Convert.ToDouble(r["Length"])).ToList();
        Assert.That(lengths, Is.Ordered.Ascending);
        Assert.That(lengths[0], Is.EqualTo(0.0)); // _private: 5-5
        Assert.That(lengths[4], Is.EqualTo(49.0)); // MyClass: 50-1
    }

    [Test]
    public async Task StringConcat_WithColumns_ReturnsConcatenated()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name || ':' || Kind AS combined FROM SymbolRecord WHERE Kind = 'Class' ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["combined"], Is.EqualTo("Helper:Class"));
        Assert.That(result.Rows[1]["combined"], Is.EqualTo("MyClass:Class"));
    }

    [Test]
    public async Task OrderBy_ColumnInRecordNotInSelect_ReturnsSorted()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord ORDER BY LineEnd");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        Assert.That(result.Rows!.Select(r => r["Name"]),
            Is.EqualTo(["_private", "IOld", "MyMethod", "Helper", "MyClass"]));
    }

    // ----- Remaining gap tests -----

    [Test]
    public async Task OrderBy_AmbiguousColumnName_ReturnsFirstMatch()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Kind AS Kind, COUNT(*) AS Kind FROM SymbolRecord GROUP BY Kind ORDER BY Kind");

        Assert.That(result.Success, Is.True);
        // Resolves to first match (Kind column) — no error, no warning
    }

    [Test]
    public async Task Subquery_ReturnsError()
    {
        var (store, registry, service) = createServices();

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM (SELECT * FROM SymbolRecord) AS sub");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("table name"));
    }

    [Test]
    public async Task UnionQuery_ReturnsError()
    {
        var (store, registry, service) = createServices();

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord UNION SELECT Name FROM SymbolRecord");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("simple SELECT"));
    }

    [Test]
    public async Task CaseExpression_EvaluatesSearchedCase()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        // CASE now evaluates properly
        var result = await service.ExecuteAsync(store,
            "SELECT CASE WHEN Kind = 'Class' THEN 'yes' ELSE 'no' END AS verdict FROM SymbolRecord ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows, Has.Count.EqualTo(5));
        // Sorted by Name: _private(Field→no), Helper(Class→yes), IOld(Interface→no), MyClass(Class→yes), MyMethod(Method→no)
        Assert.That(result.Rows![0]["verdict"], Is.EqualTo("no"));
        Assert.That(result.Rows[1]["verdict"], Is.EqualTo("yes"));
        Assert.That(result.Rows[2]["verdict"], Is.EqualTo("no"));
        Assert.That(result.Rows[3]["verdict"], Is.EqualTo("yes"));
        Assert.That(result.Rows[4]["verdict"], Is.EqualTo("no"));
    }

    [Test]
    public async Task CoalesceExpression_ResolvesFirstNonNull()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        // COALESCE now resolves via evaluateExpression
        var result = await service.ExecuteAsync(store,
            "SELECT COALESCE(Modifiers, 'none') AS fallback FROM SymbolRecord ORDER BY Name LIMIT 1");

        Assert.That(result.Success, Is.True);
        // First row sorted by Name is _private with Modifiers = "private"
        Assert.That(result.Rows![0]["fallback"], Is.EqualTo("private"));
    }

    [Test]
    public async Task CastExpression_ConvertsToString()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        // CAST now evaluates via evaluateExpression; TEXT cast returns .ToString()
        var result = await service.ExecuteAsync(store,
            "SELECT CAST(LineStart AS TEXT) AS conv FROM SymbolRecord ORDER BY Name LIMIT 1");

        Assert.That(result.Success, Is.True);
        // First row sorted by Name is _private with LineStart = 5
        Assert.That(result.Rows![0]["conv"], Is.EqualTo("5"));
    }

    [Test]
    public async Task MixedTypeOrderBy_HandlesConvertToDouble()
    {
        var (store, registry, service) = createServices();
        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:stringVal",
            Name = "StringVal",
            Kind = "999",
            FilePath = "/src/x.cs",
            FullName = "StringVal",
            LineStart = 1,
            LineEnd = 2
        });
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:numVal",
            Name = "NumVal",
            Kind = "123",
            FilePath = "/src/x.cs",
            FullName = "NumVal",
            LineStart = 1,
            LineEnd = 2
        });

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord WHERE Kind IN ('999', '123') ORDER BY Kind");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["Name"], Is.EqualTo("NumVal")); // 123 < 999
    }

    [Test]
    public async Task CountColumn_WithEmptyString_IncludesIt()
    {
        var (store, registry, service) = createServices();
        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:emptyMod",
            Name = "EmptyMod",
            Kind = "Test",
            FilePath = "/src/x.cs",
            FullName = "EmptyMod",
            LineStart = 1,
            LineEnd = 2,
            Modifiers = ""
        });
        await coll.UpsertAsync(new SymbolRecord
        {
            Id = "s:nullMod",
            Name = "NullMod",
            Kind = "Test",
            FilePath = "/src/x.cs",
            FullName = "NullMod",
            LineStart = 1,
            LineEnd = 2
        });

        var result = await service.ExecuteAsync(store,
            "SELECT COUNT(Modifiers) AS cnt FROM SymbolRecord WHERE Kind = 'Test' GROUP BY Kind");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        // Empty string is included, null is excluded
        Assert.That((long)result.Rows![0]["cnt"], Is.EqualTo(1));
    }

    [Test]
    public async Task DeepWhereExpressionTree_WorksCorrectly()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name FROM SymbolRecord WHERE " +
            "Kind = 'Class' AND Kind = 'Class' AND Kind = 'Class' AND Kind = 'Class' " +
            "AND Kind = 'Class' AND Kind = 'Class' AND Kind = 'Class' AND Kind = 'Class' " +
            "ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
    }

    [Test]
    public async Task SqlCommentBeforeQuery_Succeeds()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        // The parser ignores SQL comments before the statement
        var result = await service.ExecuteAsync(store,
            "-- comment\nSELECT * FROM SymbolRecord LIMIT 1");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
    }

    [Test]
    public async Task MultipleStatements_ReturnsError()
    {
        var (store, registry, service) = createServices();

        var result = await service.ExecuteAsync(store,
            "SELECT 1; SELECT 2");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("single-statement"));
    }

    [Test]
    public async Task Distinct_WithComputedExpression_EvaluatesInline()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        // DISTINCT now evaluates computed expressions inline,
        // so each row's LineEnd - LineStart value is compared
        var result = await service.ExecuteAsync(store,
            "SELECT DISTINCT LineEnd - LineStart FROM SymbolRecord");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
    }

    // --- materializeAsync / toAsyncEnumerable coverage ---

    [Test]
    public async Task Select_NoMatchingRows_ReturnsEmpty()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM SymbolRecord WHERE Kind = 'Nonexistent'");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(0));
        Assert.That(result.Rows, Is.Empty);
    }

    [Test]
    public async Task VectorSearch_WithLimitZero_ReturnsEmpty()
    {
        var (store, registry, service) = createServices();
        await seedChunksAsync(store);

        // LIMIT 0 passes top=0 to SearchAsync, producing an empty IAsyncEnumerable
        var result = await service.ExecuteAsync(store,
            "SELECT FilePath FROM ChunkRecord WHERE Content LIKE '%class%' ORDER BY Similarity DESC LIMIT 0");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(0));
        Assert.That(result.Rows, Is.Empty);
    }

    [Test]
    public async Task VectorSearch_MultipleMatches_ReturnsScored()
    {
        var (store, registry, service) = createServices();
        await seedChunksAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, Content FROM ChunkRecord WHERE Content LIKE '%class%' ORDER BY Similarity DESC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.GreaterThanOrEqualTo(2));
        Assert.That(result.Rows![0], Contains.Key("__score"));
    }

    [Test]
    public async Task Select_MultipleOrderByDesc_ExercisesMaterializePath()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT Name, Kind FROM SymbolRecord ORDER BY Kind DESC, Name ASC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        Assert.That(result.Rows![0]["Kind"], Is.Not.EqualTo(result.Rows![4]["Kind"]));
    }

    [Test]
    public async Task Cte_BasicSelect_ReturnsRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "WITH classes AS (SELECT * FROM SymbolRecord WHERE Kind = 'Class') SELECT Name FROM classes ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows!.Select(r => r["Name"]), Is.EqualTo(["Helper", "MyClass"]));
    }

    [Test]
    public async Task Cte_ChainedReference_ReturnsRows()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "WITH classes AS (SELECT * FROM SymbolRecord WHERE Kind = 'Class'), public_classes AS (SELECT * FROM classes WHERE Modifiers = 'public') SELECT Name FROM public_classes");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Rows![0]["Name"], Is.EqualTo("MyClass"));
    }


    [Test]
    public async Task Cte_WithProjectionAliasAndLimit_HonorsCteShape()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "WITH s AS (SELECT Name AS n FROM SymbolRecord ORDER BY Name LIMIT 2) SELECT * FROM s ORDER BY n");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Columns, Is.EquivalentTo(["n"]));
        Assert.That(result.Rows!.Select(r => r["n"]), Is.EqualTo(["Helper", "IOld"]));
    }

    [Test]
    public async Task Cte_WhereLike_FilterBehavesLikeBaseTable()
    {
        var (store, registry, service) = createServices();
        await seedSymbolsAsync(store);

        var result = await service.ExecuteAsync(store,
            "WITH s AS (SELECT * FROM SymbolRecord) SELECT Name FROM s WHERE FilePath LIKE '%MyClass%' ORDER BY Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        Assert.That(result.Rows!.Select(r => r["Name"]), Is.EqualTo(["MyClass", "MyMethod", "_private"]));
    }

    [Test]
    public async Task Cte_Recursive_ReturnsError()
    {
        var (store, registry, service) = createServices();

        var result = await service.ExecuteAsync(store,
            "WITH RECURSIVE nums AS (SELECT 1 AS n) SELECT * FROM nums");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Recursive CTEs not yet supported"));
    }

}
