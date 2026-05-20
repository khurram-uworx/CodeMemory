using CodeMemory.Mcp.SqlQuery;
using CodeMemory.Storage;
using Memori.Storage;

namespace CodeMemory.Tests.Services.Query;

public sealed class SqlQueryServiceJoinTests
{
    static async Task seedJoinDataAsync(InMemoryVectorStore store)
    {
        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord { Id = "s:MyClass", Name = "MyClass", Kind = "Class", FilePath = "/src/MyClass.cs", FullName = "MyClass", LineStart = 1, LineEnd = 100, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:MyMethod", Name = "MyMethod", Kind = "Method", FilePath = "/src/MyClass.cs", FullName = "MyClass.MyMethod", LineStart = 10, LineEnd = 30, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:MyMethod2", Name = "MyMethod2", Kind = "Method", FilePath = "/src/MyClass.cs", FullName = "MyClass.MyMethod2", LineStart = 35, LineEnd = 55, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:Helper", Name = "Helper", Kind = "Class", FilePath = "/src/Helper.cs", FullName = "Helper", LineStart = 1, LineEnd = 50, Modifiers = "internal" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:HelperMethod", Name = "HelperMethod", Kind = "Method", FilePath = "/src/Helper.cs", FullName = "Helper.HelperMethod", LineStart = 5, LineEnd = 25, Modifiers = "public" });
        await sym.UpsertAsync(new SymbolRecord { Id = "s:IOld", Name = "IOld", Kind = "Interface", FilePath = "/src/IOld.cs", FullName = "IOld", LineStart = 1, LineEnd = 10, Modifiers = "public" });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call1", SourceSymbolId = "s:IOld", TargetSymbolId = "s:MyClass", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call2", SourceSymbolId = "s:Helper", TargetSymbolId = "s:MyClass", RelationshipType = "References" });
        await rel.UpsertAsync(new RelationshipRecord { Id = "r:call3", SourceSymbolId = "s:IOld", TargetSymbolId = "s:Helper", RelationshipType = "References" });
    }

    [Test]
    public async Task CrossJoin_MostUsedSymbol()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, COUNT(*) AS cnt FROM SymbolRecord s, RelationshipRecord r " +
            "WHERE s.Id = r.TargetSymbolId GROUP BY s.Name ORDER BY cnt DESC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Columns, Does.Contain("s.Name"));
        Assert.That(result.Columns, Does.Contain("cnt"));
        Assert.That((long)result.Rows![0]["cnt"], Is.EqualTo(2));
        Assert.That(result.Rows[0]["s.Name"], Is.EqualTo("MyClass"));
        Assert.That((long)result.Rows[1]["cnt"], Is.EqualTo(1));
        Assert.That(result.Rows[1]["s.Name"], Is.EqualTo("Helper"));
    }

    [Test]
    public async Task CrossJoin_MostUsedClass()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, COUNT(*) AS cnt FROM SymbolRecord s, RelationshipRecord r " +
            "WHERE s.Id = r.TargetSymbolId AND s.Kind = 'Class' GROUP BY s.Name ORDER BY cnt DESC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["s.Name"], Is.EqualTo("MyClass"));
        Assert.That((long)result.Rows[0]["cnt"], Is.EqualTo(2));
        Assert.That(result.Rows[1]["s.Name"], Is.EqualTo("Helper"));
        Assert.That((long)result.Rows[1]["cnt"], Is.EqualTo(1));
    }

    [Test]
    public async Task CrossJoin_ClassWithMostPublicMethods()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT c.Name, COUNT(*) AS cnt FROM SymbolRecord c, SymbolRecord m " +
            "WHERE c.Kind = 'Class' AND m.Kind = 'Method' AND m.Modifiers LIKE '%public%' " +
            "AND m.FullName LIKE c.FullName || '.%' GROUP BY c.Name ORDER BY cnt DESC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["c.Name"], Is.EqualTo("MyClass"));
        Assert.That((long)result.Rows[0]["cnt"], Is.EqualTo(2));
        Assert.That(result.Rows[1]["c.Name"], Is.EqualTo("Helper"));
        Assert.That((long)result.Rows[1]["cnt"], Is.EqualTo(1));
    }

    [Test]
    public async Task CrossJoin_WithCteAndJoin_Works()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "WITH classes AS (SELECT * FROM SymbolRecord WHERE Kind = 'Class') " +
            "SELECT c.Name, COUNT(*) AS cnt FROM classes c, SymbolRecord m " +
            "WHERE m.Kind = 'Method' AND m.Modifiers LIKE '%public%' " +
            "AND m.FullName LIKE c.FullName || '.%' GROUP BY c.Name ORDER BY cnt DESC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["c.Name"], Is.EqualTo("MyClass"));
        Assert.That((long)result.Rows[0]["cnt"], Is.EqualTo(2));
    }

    [Test]
    public async Task CrossJoin_WithExplicitJoinSyntax_Works()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, COUNT(*) AS cnt FROM SymbolRecord s " +
            "JOIN RelationshipRecord r ON s.Id = r.TargetSymbolId " +
            "GROUP BY s.Name ORDER BY cnt DESC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["s.Name"], Is.EqualTo("MyClass"));
        Assert.That((long)result.Rows[0]["cnt"], Is.EqualTo(2));
    }

    [Test]
    public async Task CrossJoin_VectorSearch_Rejected()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM SymbolRecord s, ChunkRecord c WHERE c.Content LIKE '%auth%' ORDER BY Similarity DESC");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("not supported with multi-table"));
    }

    [Test]
    public async Task CrossJoin_OrderByWithoutGroupBy()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, r.RelationshipType FROM SymbolRecord s, RelationshipRecord r " +
            "WHERE s.Id = r.TargetSymbolId ORDER BY s.Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        Assert.That(result.Rows![0]["s.Name"], Is.EqualTo("Helper"));
        Assert.That(result.Rows[1]["s.Name"], Is.EqualTo("MyClass"));
        Assert.That(result.Rows[2]["s.Name"], Is.EqualTo("MyClass"));
        Assert.That(result.Columns, Does.Contain("s.Name"));
        Assert.That(result.Columns, Does.Contain("r.RelationshipType"));
    }

    [Test]
    public async Task CrossJoin_Having()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, COUNT(*) AS cnt FROM SymbolRecord s, RelationshipRecord r " +
            "WHERE s.Id = r.TargetSymbolId GROUP BY s.Name HAVING cnt > 1 ORDER BY cnt DESC");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Rows![0]["s.Name"], Is.EqualTo("MyClass"));
        Assert.That((long)result.Rows[0]["cnt"], Is.EqualTo(2));
    }

    [Test]
    public async Task CrossJoin_Distinct()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);
        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord
        { Id = "r:call4", SourceSymbolId = "s:MyClass", TargetSymbolId = "s:IOld", RelationshipType = "References" });

        var result = await service.ExecuteAsync(store,
            "SELECT DISTINCT s.Kind FROM SymbolRecord s, RelationshipRecord r WHERE s.Id = r.TargetSymbolId ORDER BY s.Kind");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["s.Kind"], Is.EqualTo("Class"));
        Assert.That(result.Rows[1]["s.Kind"], Is.EqualTo("Interface"));
    }

    [Test]
    public async Task CrossJoin_WildcardSelect()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT * FROM SymbolRecord s, RelationshipRecord r WHERE s.Id = r.TargetSymbolId LIMIT 1");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(1));
        Assert.That(result.Columns, Does.Contain("s.Id"));
        Assert.That(result.Columns, Does.Contain("s.Name"));
        Assert.That(result.Columns, Does.Contain("r.TargetSymbolId"));
        Assert.That(result.Columns, Does.Contain("r.RelationshipType"));
    }

    [Test]
    public async Task CrossJoin_ThreeTables()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT a.Name, b.Name, r.RelationshipType FROM SymbolRecord a, SymbolRecord b, RelationshipRecord r " +
            "WHERE a.Id = r.TargetSymbolId AND b.Id = r.SourceSymbolId ORDER BY a.Name, b.Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        Assert.That(result.Rows!.Any(r => r["a.Name"]!.ToString() == "MyClass" && r["b.Name"]!.ToString() == "IOld"), Is.True);
        Assert.That(result.Rows.Any(r => r["a.Name"]!.ToString() == "MyClass" && r["b.Name"]!.ToString() == "Helper"), Is.True);
        Assert.That(result.Rows.Any(r => r["a.Name"]!.ToString() == "Helper" && r["b.Name"]!.ToString() == "IOld"), Is.True);
    }

    [Test]
    public async Task CrossJoin_CrossJoinSyntax()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name FROM SymbolRecord s CROSS JOIN RelationshipRecord r WHERE s.Id = r.TargetSymbolId GROUP BY s.Name ORDER BY s.Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["s.Name"], Is.EqualTo("Helper"));
        Assert.That(result.Rows[1]["s.Name"], Is.EqualTo("MyClass"));
    }

    [Test]
    public async Task CrossJoin_LeftJoinSyntax()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, COUNT(*) AS cnt FROM SymbolRecord s LEFT JOIN RelationshipRecord r ON s.Id = r.TargetSymbolId WHERE s.Kind = 'Class' GROUP BY s.Name ORDER BY s.Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows![0]["s.Name"], Is.EqualTo("Helper"));
        Assert.That(result.Rows[1]["s.Name"], Is.EqualTo("MyClass"));
    }

    [Test]
    public async Task CrossJoin_WhereOnSingleTableColumn()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, r.RelationshipType FROM SymbolRecord s, RelationshipRecord r " +
            "WHERE s.Id = r.TargetSymbolId AND s.Name = 'MyClass' ORDER BY r.RelationshipType");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        foreach (var row in result.Rows!)
            Assert.That(row["s.Name"], Is.EqualTo("MyClass"));
    }
}
