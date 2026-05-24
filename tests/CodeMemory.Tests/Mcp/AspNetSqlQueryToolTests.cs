using CodeMemory.AspNet.Storage;
using CodeMemory.AspNet.Tools;
using CodeMemory.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace CodeMemory.Tests.Mcp;

public sealed class AspNetSqlQueryToolTests : BaseToolTests
{
    [Test]
    public async Task SqlQueryTool_AppearsInAspNetDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Storage:Provider"] = "inmemory",
                        ["Repositories:codememory"] = "."
                    });
                });
            });

        var client = factory.CreateClient();
        var result = await SendToolsList(client);

        var tools = result["result"]?["tools"]?.AsArray();
        var toolNames = tools!.Select(tool => tool!["name"]?.GetValue<string>()).ToList();

        Assert.That(toolNames, Does.Contain("sql_query"));
    }

    [Test]
    public async Task SqlQueryAsync_ReturnsSymbolRows()
    {
        var (tool, storage, tempDir) = await CreateToolWithData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT Id AS "Id", Name AS "Name", Kind AS "Kind" FROM SymbolRecord WHERE Kind = 'Class' ORDER BY Name""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 1);
            var rows = GetRows(result);
            Assert.That(rows[0]["Id"], Is.EqualTo("symbol-1"));
            Assert.That(rows[0]["Name"], Is.EqualTo("TestClass"));
            Assert.That(rows[0]["Kind"], Is.EqualTo("Class"));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [Test]
    public async Task SqlQueryAsync_ReturnsRelationshipRows()
    {
        var (tool, storage, tempDir) = await CreateToolWithData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT Id AS "Id", SourceSymbolId AS "SourceSymbolId", TargetSymbolId AS "TargetSymbolId" FROM RelationshipRecord WHERE RelationshipType = 'References'""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 1);
            var rows = GetRows(result);
            Assert.That(rows[0]["Id"], Is.EqualTo("rel-1"));
            Assert.That(rows[0]["SourceSymbolId"], Is.EqualTo("symbol-1"));
            Assert.That(rows[0]["TargetSymbolId"], Is.EqualTo("symbol-2"));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [TestCase("SELECT * FROM ChunkRecord", "ChunkRecord queries not supported")]
    [TestCase("DELETE FROM SymbolRecord", "Only SELECT statements are supported")]
    public async Task SqlQueryAsync_RejectsUnsupportedQueryShapes(string sql, string expectedError)
    {
        var (tool, storage, tempDir) = await CreateToolWithData();

        try
        {
            var result = await tool.SqlQueryAsync(sql);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain(expectedError));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [Test]
    public async Task SqlQueryAsync_JoinQuery_ReturnsRows()
    {
        var (tool, storage, tempDir) = await CreateToolWithData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT s.Name AS "Name", r.RelationshipType AS "RelationshipType" FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId ORDER BY s.Name""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 1);
            var rows = GetRows(result);
            Assert.That(rows[0]["Name"], Is.EqualTo("TestClass"));
            Assert.That(rows[0]["RelationshipType"], Is.EqualTo("References"));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [Test]
    public async Task SqlQueryAsync_Subquery_ReturnsRows()
    {
        var (tool, storage, tempDir) = await CreateToolWithData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT * FROM SymbolRecord WHERE Id IN (SELECT SourceSymbolId FROM RelationshipRecord WHERE RelationshipType = 'References')""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 1);
            var rows = GetRows(result);
            Assert.That(rows[0]["Id"], Is.EqualTo("symbol-1"));
            Assert.That(rows[0]["Name"], Is.EqualTo("TestClass"));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    static async Task<(AspNetSqlQueryTool Tool, HybridStorageService Storage, string TempDir)> CreateToolWithData()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();
        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "symbol-1",
                Name = "TestClass",
                Kind = "Class",
                FilePath = "/src/Test.cs",
                FullName = "TestClass"
            },
            new SymbolRecord
            {
                Id = "symbol-2",
                Name = "OtherClass",
                Kind = "Interface",
                FilePath = "/src/Other.cs",
                FullName = "OtherClass"
            }
        ]);
        await storage.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "rel-1",
                SourceSymbolId = "symbol-1",
                TargetSymbolId = "symbol-2",
                RelationshipType = "References"
            }
        ]);

        return (new AspNetSqlQueryTool(storage, NullLogger<AspNetSqlQueryTool>.Instance), storage, tempDir);
    }

    static HybridStorageService CreateStorage(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "CodeMemoryAspNetSqlQueryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "query.db");
        var connectionString = $"Data Source={dbPath}";
        var store = new SqliteVectorStore(connectionString);
        var options = new DbContextOptionsBuilder<CodeMemoryDbContext>()
            .UseSqlite(connectionString)
            .ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>()
            .Options;

        return new HybridStorageService(
            tempDir,
            NullLogger<HybridStorageService>.Instance,
            store,
            () => new CodeMemoryDbContext(options, "main"),
            configuredDimension: TestConstants.EmbeddingDimension);
    }

    static void AssertSuccess(AspNetSqlQueryResult result, int expectedRowCount)
    {
        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.EqualTo(expectedRowCount));
        Assert.That(result.Error, Is.Null);
    }

    static List<Dictionary<string, object?>> GetRows(AspNetSqlQueryResult result)
        => result.Rows!;

    // ── Expression-inside-aggregate integration tests ──

    static async Task<(AspNetSqlQueryTool Tool, HybridStorageService Storage, string TempDir)> CreateToolWithLineData()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();
        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "sym-a",
                Name = "ClassA",
                Kind = "Class",
                FilePath = "/src/A.cs",
                FullName = "ClassA",
                LineStart = 1,
                LineEnd = 50
            },
            new SymbolRecord
            {
                Id = "sym-b",
                Name = "ClassB",
                Kind = "Class",
                FilePath = "/src/B.cs",
                FullName = "ClassB",
                LineStart = 10,
                LineEnd = 40
            },
            new SymbolRecord
            {
                Id = "sym-c",
                Name = "MyMethod",
                Kind = "Method",
                FilePath = "/src/A.cs",
                FullName = "ClassA.MyMethod",
                LineStart = 5,
                LineEnd = 15
            }
        ]);

        return (new AspNetSqlQueryTool(storage, NullLogger<AspNetSqlQueryTool>.Instance), storage, tempDir);
    }

    [Test]
    public async Task SqlQueryAsync_AggregateExpression_AvgWithArithmetic_ReturnsCorrectValue()
    {
        var (tool, storage, tempDir) = await CreateToolWithLineData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT Kind, AVG(LineEnd - LineStart) AS avgLen FROM SymbolRecord GROUP BY Kind ORDER BY Kind""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 2);
            var byKind = GetRows(result).ToDictionary(r => (string)r["Kind"]!);
            Assert.That(Convert.ToDouble(byKind["Class"]["avgLen"]), Is.EqualTo(39.5));
            Assert.That(Convert.ToDouble(byKind["Method"]["avgLen"]), Is.EqualTo(10.0));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [Test]
    public async Task SqlQueryAsync_AggregateExpression_SumMinMaxWithArithmetic_ReturnsCorrectValues()
    {
        var (tool, storage, tempDir) = await CreateToolWithLineData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT Kind, SUM(LineEnd - LineStart) AS total, MIN(LineEnd - LineStart) AS minLen, MAX(LineEnd - LineStart) AS maxLen FROM SymbolRecord GROUP BY Kind ORDER BY Kind""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 2);
            var byKind = GetRows(result).ToDictionary(r => (string)r["Kind"]!);
            Assert.That(Convert.ToDouble(byKind["Class"]["total"]), Is.EqualTo(79.0));
            Assert.That(Convert.ToDouble(byKind["Class"]["minLen"]), Is.EqualTo(30.0));
            Assert.That(Convert.ToDouble(byKind["Class"]["maxLen"]), Is.EqualTo(49.0));
            Assert.That(Convert.ToDouble(byKind["Method"]["total"]), Is.EqualTo(10.0));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [Test]
    public async Task SqlQueryAsync_AggregateExpression_MixedWithPlainColumns_Works()
    {
        var (tool, storage, tempDir) = await CreateToolWithLineData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT Kind, COUNT(*) AS cnt, AVG(LineEnd - LineStart) AS avgLen FROM SymbolRecord GROUP BY Kind ORDER BY Kind""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 2);
            var byKind = GetRows(result).ToDictionary(r => (string)r["Kind"]!);
            Assert.That((long)byKind["Class"]["cnt"]!, Is.EqualTo(2));
            Assert.That(Convert.ToDouble(byKind["Class"]["avgLen"]), Is.EqualTo(39.5));
            Assert.That((long)byKind["Method"]["cnt"]!, Is.EqualTo(1));
            Assert.That(Convert.ToDouble(byKind["Method"]["avgLen"]), Is.EqualTo(10.0));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [Test]
    public async Task SqlQueryAsync_AggregateExpression_GlobalNoGroupBy_Works()
    {
        var (tool, storage, tempDir) = await CreateToolWithLineData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT AVG(LineEnd - LineStart) AS overall FROM SymbolRecord""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 1);
            Assert.That(Convert.ToDouble(GetRows(result)[0]["overall"]), Is.EqualTo(29.666666666666668));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    [Test]
    public async Task SqlQueryAsync_AggregateExpression_WithAlias_ReturnedInColumns()
    {
        var (tool, storage, tempDir) = await CreateToolWithLineData();

        try
        {
            var result = await tool.SqlQueryAsync(
                """SELECT Kind, AVG(LineEnd - LineStart) AS length FROM SymbolRecord GROUP BY Kind ORDER BY Kind""",
                maxResults: 10);

            AssertSuccess(result, expectedRowCount: 2);
            Assert.That(result.Columns, Does.Contain("length"));
        }
        finally
        {
            storage.Dispose();
            Cleanup(tempDir);
        }
    }

    static void Cleanup(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for handles held by SQLite/vector store providers.
        }
    }
}
