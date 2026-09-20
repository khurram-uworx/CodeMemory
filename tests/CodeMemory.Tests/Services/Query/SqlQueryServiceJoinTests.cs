using CodeMemory.Storage;
using Memori.Storage;
using System.Diagnostics;

namespace CodeMemory.Tests.Services.Query;

public sealed class SqlQueryServiceJoinTests
{
    static async Task seedJoinDataAsync(InMemoriVectorStore store)
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
        Assert.That((long)result.Rows![0]["cnt"]!, Is.EqualTo(2));
        Assert.That(result.Rows[0]["s.Name"], Is.EqualTo("MyClass"));
        Assert.That((long)result.Rows[1]["cnt"]!, Is.EqualTo(1));
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
        Assert.That((long)result.Rows[0]["cnt"]!, Is.EqualTo(2));
        Assert.That(result.Rows[1]["s.Name"], Is.EqualTo("Helper"));
        Assert.That((long)result.Rows[1]["cnt"]!, Is.EqualTo(1));
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
        Assert.That((long)result.Rows[0]["cnt"]!, Is.EqualTo(2));
        Assert.That(result.Rows[1]["c.Name"], Is.EqualTo("Helper"));
        Assert.That((long)result.Rows[1]["cnt"]!, Is.EqualTo(1));
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
        Assert.That((long)result.Rows[0]["cnt"]!, Is.EqualTo(2));
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
        Assert.That((long)result.Rows[0]["cnt"]!, Is.EqualTo(2));
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
        Assert.That((long)result.Rows[0]["cnt"]!, Is.EqualTo(2));
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
        Assert.That(result.Rows!.Any(r => r["a.Name"]!.ToString() == "MyClass" && r["b.Name"]!.ToString() == "Helper"), Is.True);
        Assert.That(result.Rows!.Any(r => r["a.Name"]!.ToString() == "Helper" && r["b.Name"]!.ToString() == "IOld"), Is.True);
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

    [Test]
    public async Task CrossJoin_RightJoinSyntax_ReturnsRightOuterRows()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);
        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord
        {
            Id = "r:orphan",
            SourceSymbolId = "s:NONEXISTENT",
            TargetSymbolId = "s:NONEXISTENT",
            RelationshipType = "Orphan"
        });

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, r.RelationshipType FROM SymbolRecord s RIGHT JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId ORDER BY r.Id");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(4));
        var lastRow = result.Rows![3];
        Assert.That(lastRow["s.Name"], Is.Null);
        Assert.That(lastRow["r.RelationshipType"], Is.EqualTo("Orphan"));
    }

    [Test]
    public async Task CrossJoin_FullOuterJoinSyntax_ReturnsAllRows()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);
        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord
        {
            Id = "s:Loner",
            Name = "Loner",
            Kind = "Class",
            FilePath = "/src/Loner.cs",
            FullName = "Loner",
            LineStart = 1,
            LineEnd = 10,
            Modifiers = "public"
        });
        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord
        {
            Id = "r:orphan",
            SourceSymbolId = "s:NONEXISTENT",
            TargetSymbolId = "s:Loner",
            RelationshipType = "Orphan"
        });

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, r.RelationshipType FROM SymbolRecord s FULL OUTER JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId ORDER BY s.Name");

        Assert.That(result.Success, Is.True);
        // Loner (left orphan, null right), Helper/IOld/MyClass/etc (matched), orphan (right orphan, null left)
        var rows = result.Rows!;
        Assert.That(rows.Any(r => r["s.Name"]?.ToString() == "Loner" && r["r.RelationshipType"] is null), Is.True);
        Assert.That(rows.Any(r => r["s.Name"] is null && r["r.RelationshipType"]?.ToString() == "Orphan"), Is.True);
        Assert.That(rows.Any(r => r["s.Name"]?.ToString() == "Helper" && r["r.RelationshipType"]?.ToString() == "References"), Is.True);
    }

    [Test]
    public async Task CrossJoin_UsingSyntax_GeneratesEqualityCondition()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT a.Name FROM SymbolRecord a JOIN SymbolRecord b USING(Id) ORDER BY a.Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(6));
        Assert.That(result.Columns, Does.Contain("a.Name"));
    }

    [Test]
    public async Task CrossJoin_NestedJoinSyntax_EvaluatesCorrectly()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT a.Name, b.Name FROM SymbolRecord a JOIN (SymbolRecord b JOIN RelationshipRecord r ON b.Id = r.SourceSymbolId) ON a.Id = r.TargetSymbolId");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(3));
        Assert.That(result.Rows!.Any(r => r["a.Name"]?.ToString() == "MyClass" && r["b.Name"]?.ToString() == "IOld"), Is.True);
        Assert.That(result.Rows!.Any(r => r["a.Name"]?.ToString() == "MyClass" && r["b.Name"]?.ToString() == "Helper"), Is.True);
        Assert.That(result.Rows!.Any(r => r["a.Name"]?.ToString() == "Helper" && r["b.Name"]?.ToString() == "IOld"), Is.True);
    }

    [Test]
    public async Task JoinOn_UnknownColumn_ReturnsClearError()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.Nope");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Unknown column 'r.Nope'"));
        Assert.That(result.Error, Does.Contain("Available columns on 'RelationshipRecord' (alias r)"));
        Assert.That(result.Error, Does.Contain("SourceSymbolId"));
    }

    [Test]
    public async Task JoinOn_UnknownAlias_ReturnsClearError()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name FROM SymbolRecord s JOIN RelationshipRecord r ON z.Id = r.SourceSymbolId");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Unknown table alias 'z'"));
        Assert.That(result.Error, Does.Contain("Available aliases: s (SymbolRecord), r (RelationshipRecord)"));
    }

    [Test]
    public async Task Select_UnknownColumn_MultiTableJoin_ReturnsClearError()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, r.Nope FROM SymbolRecord s, RelationshipRecord r WHERE s.Id = r.TargetSymbolId");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Unknown column 'r.Nope'"));
        Assert.That(result.Error, Does.Contain("Available columns on 'RelationshipRecord' (alias r)"));
    }

    [Test]
    public async Task Where_UnqualifiedColumn_MultiTable_RequiresQualification()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name FROM SymbolRecord s, RelationshipRecord r WHERE Kind = 'Class'");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Unknown column 'Kind'"));
        Assert.That(result.Error, Does.Contain("table-qualified"));
        Assert.That(result.Error, Does.Contain("Available aliases"));
        Assert.That(result.Error, Does.Contain("s (SymbolRecord)"));
        Assert.That(result.Error, Does.Contain("r (RelationshipRecord)"));
    }

    [Test]
    public async Task OrderBy_UnknownAlias_MultiTable_ReturnsClearError()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name FROM SymbolRecord s, RelationshipRecord r WHERE s.Id = r.TargetSymbolId ORDER BY z.Name");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Unknown table alias 'z'"));
        Assert.That(result.Error, Does.Contain("qualify as e.g. 's.Id'"));
    }

    [Test]
    public async Task Join_Where_ParenthesizedOrGroup_FiltersCorrectly()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        // Issue #123 shape evaluated on merged alias-prefixed rows: paren-wrapped OR across
        // qualified columns of both tables. IOld->MyClass matches neither branch and is excluded.
        var result = await service.ExecuteAsync(store,
            "SELECT s.Name FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId " +
            "WHERE (s.Kind = 'Class' OR r.TargetSymbolId = 's:Helper') ORDER BY s.Name");

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(2));
        Assert.That(result.Rows!.Select(r => r["s.Name"]), Is.EquivalentTo(["Helper", "IOld"]));
    }

    // ── Issue #126 regression: JOINs at index scale must not hang ──

    static async Task seedScaleJoinDataAsync(InMemoriVectorStore store, int symbolCount, int relationshipCount)
    {
        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        for (int i = 0; i < symbolCount; i++)
            await sym.UpsertAsync(new SymbolRecord
            {
                Id = $"s:{i}",
                Name = $"Sym{i:D6}",
                Kind = i % 2 == 0 ? "Class" : "Method",
                FilePath = $"/src/module{i % 50}.cs",
                FullName = $"Namespace.Sym{i}",
                LineStart = i % 100,
                LineEnd = (i % 100) + 8,
                Modifiers = "public"
            });

        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        for (int i = 0; i < relationshipCount; i++)
            await rel.UpsertAsync(new RelationshipRecord
            {
                Id = $"r:{i}",
                SourceSymbolId = $"s:{i % symbolCount}",
                TargetSymbolId = $"s:{(i * 7) % symbolCount}",
                RelationshipType = (i % 3) switch { 0 => "Calls", 1 => "References", _ => "Inherits" }
            });
    }

    [Test]
    public async Task JoinScale_ReportedQueryWithLimit5_ReturnsFiveRowsQuickly()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedScaleJoinDataAsync(store, symbolCount: 3_000, relationshipCount: 10_000);

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT r.RelationshipType, s.Name AS SourceName, t.Name AS TargetName, t.FilePath AS TargetPath " +
            "FROM RelationshipRecord r JOIN SymbolRecord s ON r.SourceSymbolId = s.Id " +
            "JOIN SymbolRecord t ON r.TargetSymbolId = t.Id LIMIT 5");
        sw.Stop();

        // Nested-loop joins (pre-fix) ran 2 × 10_000 × 3_000 pair evaluations here —
        // tens of seconds. The hash equi-join must return the 5 rows in well under 5s.
        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        Assert.That(result.Rows!.All(r => r["SourceName"] is not null && r["TargetName"] is not null));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"LIMIT 5 join took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Test]
    public async Task JoinScale_FullClosureCount_CompletesUnderBudget()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedScaleJoinDataAsync(store, symbolCount: 3_000, relationshipCount: 10_000);

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT COUNT(*) AS total FROM RelationshipRecord r " +
            "JOIN SymbolRecord s ON r.SourceSymbolId = s.Id " +
            "JOIN SymbolRecord t ON r.TargetSymbolId = t.Id");
        sw.Stop();

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows![0]["total"], Is.EqualTo(10_000L));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"full-closure join took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Test]
    public async Task JoinOn_WrongColumnName_Issue126_ReturnsErrorNotHang()
    {
        // v0.6.0 silently evaluated the misspelled join key as NULL and (at scale) looked
        // like another hang; schema-first validation must fail fast instead (see #122).
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedScaleJoinDataAsync(store, symbolCount: 3_000, relationshipCount: 10_000);

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT s.Name FROM RelationshipRecord r JOIN SymbolRecord s ON r.SourceId = s.Id LIMIT 5");
        sw.Stop();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Unknown column 'r.SourceId'"));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"validation took {sw.Elapsed.TotalSeconds:F1}s");
    }

    // ── Issue #137 regression: RIGHT/FULL OUTER and non-equi joins at index scale ──

    [Test]
    public async Task JoinScale_RightOuterCount_CompletesUnderBudget()
    {
        // Pre-fix, RIGHT OUTER fell back to leftJoin(right, left) — a 10k × 3k nested loop
        // (~27s at live-index scale for a 4.4k × 2.5k index). The reverse-probe hash join must
        // complete the full closure well under 5s. Every relationship matches its source symbol,
        // so the count is the full 10k relationship closure.
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedScaleJoinDataAsync(store, symbolCount: 3_000, relationshipCount: 10_000);

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT COUNT(*) AS total FROM RelationshipRecord r RIGHT JOIN SymbolRecord s ON r.SourceSymbolId = s.Id");
        sw.Stop();

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows![0]["total"], Is.EqualTo(10_000L));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"RIGHT JOIN took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Test]
    public async Task JoinScale_FullOuterCount_CompletesUnderBudget()
    {
        // Pre-fix FULL OUTER ran two nested loops and a values-order-dependent dedup that
        // double-counted matched pairs (9800 vs 5404 on the live index). The hash FULL outer must
        // complete the closure under 5s and count each pair once.
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedScaleJoinDataAsync(store, symbolCount: 3_000, relationshipCount: 10_000);

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT COUNT(*) AS total FROM RelationshipRecord r FULL OUTER JOIN SymbolRecord s ON r.SourceSymbolId = s.Id");
        sw.Stop();

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows![0]["total"], Is.EqualTo(10_000L));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"FULL OUTER JOIN took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Test]
    public async Task FullOuter_LeftAndRightOrphans_CountedExactlyOnce()
    {
        // Regression for the FULL OUTER double-count: the old two-pass implementation merged
        // dicts in opposite column order, so matched pairs deduped on a values-order key that
        // differed between passes and were emitted twice. With a lone left orphan (relationship
        // to a missing symbol) and right orphans (symbols never a source), the correct total is
        // 3 matched + 1 left orphan + 5 right orphans = 9 — the buggy engine returned 12.
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);
        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord
        {
            Id = "s:OrphanB", Name = "OrphanB", Kind = "Class", FilePath = "/src/OrphanB.cs",
            FullName = "OrphanB", LineStart = 1, LineEnd = 10, Modifiers = "public"
        });
        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord
        {
            Id = "r:orphan", SourceSymbolId = "s:NONEXISTENT", TargetSymbolId = "s:NONEXISTENT",
            RelationshipType = "Orphan"
        });

        var result = await service.ExecuteAsync(store,
            "SELECT COUNT(*) AS total FROM SymbolRecord s FULL OUTER JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows![0]["total"], Is.EqualTo(9L));
    }

    [Test]
    public async Task FullOuter_LeftAndRightOrphans_RowsResolveCorrectly()
    {
        // Same shape as above — spot-check the actual row content: the matched pair, the left
        // orphan (null symbol columns), and a right orphan (null relationship columns).
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);
        var sym = store.GetCollection<string, SymbolRecord>("symbols");
        await sym.UpsertAsync(new SymbolRecord
        {
            Id = "s:OrphanB", Name = "OrphanB", Kind = "Class", FilePath = "/src/OrphanB.cs",
            FullName = "OrphanB", LineStart = 1, LineEnd = 10, Modifiers = "public"
        });
        var rel = store.GetCollection<string, RelationshipRecord>("relationships");
        await rel.UpsertAsync(new RelationshipRecord
        {
            Id = "r:orphan", SourceSymbolId = "s:NONEXISTENT", TargetSymbolId = "s:NONEXISTENT",
            RelationshipType = "Orphan"
        });

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, r.RelationshipType FROM SymbolRecord s FULL OUTER JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId");
        var rows = result.Rows!;

        Assert.That(rows.Any(r => r["s.Name"]?.ToString() == "IOld" && r["r.RelationshipType"]?.ToString() == "References"), Is.True);
        Assert.That(rows.Any(r => r["s.Name"] is null && r["r.RelationshipType"]?.ToString() == "Orphan"), Is.True);
        Assert.That(rows.Any(r => r["s.Name"]?.ToString() == "OrphanB" && r["r.RelationshipType"] is null), Is.True);
    }

    [Test]
    public async Task JoinNonEqui_UnboundedClosure_FailsFastWithDiagnostic()
    {
        // A non-extractable ON predicate over the full closure (needsFullFetch ⇒ no row budget)
        // would run a 10k × 3k nested loop — a hang at index scale. It must fail fast with a
        // diagnostic instead of appearing to hang (issue #137).
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedScaleJoinDataAsync(store, symbolCount: 3_000, relationshipCount: 10_000);

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT COUNT(*) AS total FROM RelationshipRecord r JOIN SymbolRecord s ON r.SourceSymbolId <> s.Id");
        sw.Stop();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("would evaluate"));
        Assert.That(result.Error, Does.Contain("nested-loop limit"));
        Assert.That(result.Error, Does.Contain("equi-join"));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"non-equi rejection took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Test]
    public async Task JoinNonEqui_RightOuterWithLimit_EarlyExitsUnderBudget()
    {
        // RIGHT OUTER is right-major, so a LIMIT early-exit prefix is a valid result. The
        // budget must flow into the non-equi nested-loop arm — without it, a LIMIT 5 query
        // silently runs the full 10k × 3k pair closure (a hang at index scale, verified live
        // during #137: 135s for LIMIT 5).
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedScaleJoinDataAsync(store, symbolCount: 3_000, relationshipCount: 10_000);

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT r.Id FROM RelationshipRecord r RIGHT JOIN SymbolRecord s ON r.SourceSymbolId <> s.Id LIMIT 5");
        sw.Stop();

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(5));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), $"LIMIT early-exit took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Test]
    public async Task JoinOrderBy_QualifiedAmbiguousColumn_SortsByDeclaredSide()
    {
        // ORDER BY r.Id was silently stripped to "Id", then resolved by first ".Id"-suffixed
        // key in the merged row — which key won depended on merge/dict order, so INNER/LEFT
        // sorted by s.Id and the RIGHT hash path sorted by s.Id too (flipping the pre-fix
        // RIGHT result). The qualified name must win: rows ordered by r.Id (call1, call2, call3)
        // regardless of join side or merge layout.
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT s.Name, r.Id FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId ORDER BY r.Id");

        Assert.That(result.Success, Is.True);
        var rows = result.Rows!;
        Assert.That(rows.Count, Is.EqualTo(3));
        Assert.That(rows[0]["r.Id"], Is.EqualTo("r:call1"));
        Assert.That(rows[0]["s.Name"], Is.EqualTo("IOld"));
        Assert.That(rows[1]["r.Id"], Is.EqualTo("r:call2"));
        Assert.That(rows[1]["s.Name"], Is.EqualTo("Helper"));
        Assert.That(rows[2]["r.Id"], Is.EqualTo("r:call3"));
        Assert.That(rows[2]["s.Name"], Is.EqualTo("IOld"));
    }

    [Test]
    public async Task JoinNonEqui_SmallScale_StillEvaluates()
    {
        // The fail-fast guard must not fire for small inputs: a non-extractable RIGHT JOIN ON
        // predicate on the tiny seed still evaluates through the nested loop. RIGHT JOIN
        // preserves every symbol row; each matches every relationship except those whose source
        // equals the symbol's own Id: IOld 2, Helper 3, the other four symbols 4 each = 21.
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        await seedJoinDataAsync(store);

        var result = await service.ExecuteAsync(store,
            "SELECT COUNT(*) AS total FROM RelationshipRecord r RIGHT JOIN SymbolRecord s ON r.SourceSymbolId <> s.Id");

        // RIGHT JOIN preserves every symbol row; each matches every relationship except those
        // whose source equals the symbol's own Id: IOld 1 (call2), Helper 2 (call1, call3), the
        // other four symbols 3 each (all three relationships) — total 15.
        Assert.That(result.Success, Is.True);
        Assert.That(result.Rows![0]["total"], Is.EqualTo(15L));
    }
}
