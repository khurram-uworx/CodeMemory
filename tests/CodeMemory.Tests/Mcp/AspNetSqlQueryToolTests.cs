using CodeMemory.AspNet.Storage;
using CodeMemory.AspNet.Tools;
using CodeMemory.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    [TestCase("SELECT * FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId", "JOINs and multiple FROM tables are not supported")]
    [TestCase("DELETE FROM SymbolRecord", "Only SELECT statements are supported")]
    public async Task SqlQueryAsync_RejectsUnsupportedQueryShapes(string sql, string expectedError)
    {
        var (tool, storage, tempDir) = await CreateToolWithData();

        try
        {
            var result = await tool.SqlQueryAsync(sql);

            Assert.That(result["success"], Is.False);
            Assert.That(result["error"]?.ToString(), Does.Contain(expectedError));
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

    static void AssertSuccess(IDictionary<string, object?> result, int expectedRowCount)
    {
        Assert.That(result["success"], Is.True);
        Assert.That(result["rowCount"], Is.EqualTo(expectedRowCount));
        Assert.That(result["error"], Is.Null);
    }

    static List<Dictionary<string, object?>> GetRows(IDictionary<string, object?> result)
        => (List<Dictionary<string, object?>>)result["rows"]!;

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
