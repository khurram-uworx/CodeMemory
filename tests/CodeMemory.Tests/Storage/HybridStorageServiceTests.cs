using CodeMemory.AspNet.Storage;
using CodeMemory.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace CodeMemory.Tests.Storage;

public sealed class HybridStorageServiceTests
{
    [Test]
    public async Task Symbols_And_Relationships_RoundTripThroughRelationalStore()
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
                LineStart = 1,
                LineEnd = 20,
                FullName = "TestClass",
                Modifiers = "public",
                Documentation = "docs"
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

        var symbol = await storage.GetSymbolAsync("symbol-1");
        var relationship = await storage.GetRelationshipAsync("rel-1");

        Assert.That(symbol, Is.Not.Null);
        Assert.That(symbol!.Name, Is.EqualTo("TestClass"));
        Assert.That(symbol.Kind, Is.EqualTo("Class"));
        Assert.That(symbol.Modifiers, Is.EqualTo("public"));

        Assert.That(relationship, Is.Not.Null);
        Assert.That(relationship!.SourceSymbolId, Is.EqualTo("symbol-1"));
        Assert.That(relationship.TargetSymbolId, Is.EqualTo("symbol-2"));

        Cleanup(tempDir);
    }

    [Test]
    public async Task Chunks_RoundTripThroughVectorStore()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();

        var embedding = new float[TestConstants.EmbeddingDimension];
        embedding[0] = 1;

        await storage.StoreChunksAsync([
            new ChunkRecord
            {
                Id = "chunk-1",
                SymbolId = "symbol-1",
                FilePath = "/src/Test.cs",
                Content = "public class TestClass { }",
                Language = "CSharp",
                LineStart = 1,
                LineEnd = 5,
                Embedding = embedding.AsMemory()
            }
        ]);

        var chunk = await storage.GetChunkAsync("chunk-1");
        var results = await storage.SearchChunksAsync(embedding.AsMemory(), top: 1);

        Assert.That(chunk, Is.Not.Null);
        Assert.That(chunk!.Content, Is.EqualTo("public class TestClass { }"));
        Assert.That(chunk.Embedding, Is.Not.Null);
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Chunk.Id, Is.EqualTo("chunk-1"));

        Cleanup(tempDir);
    }

    [Test]
    public async Task StoreSymbolsAsync_UpdatesExistingRows()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "symbol-1",
                Name = "Original",
                Kind = "Class",
                FilePath = "/src/Test.cs",
                FullName = "Original"
            }
        ]);

        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "symbol-1",
                Name = "Updated",
                Kind = "Class",
                FilePath = "/src/Test.cs",
                FullName = "Updated"
            }
        ]);

        var symbol = await storage.GetSymbolAsync("symbol-1");

        Assert.That(symbol, Is.Not.Null);
        Assert.That(symbol!.Name, Is.EqualTo("Updated"));
        Assert.That(symbol.FullName, Is.EqualTo("Updated"));

        Cleanup(tempDir);
    }

    [Test]
    public async Task StoreChunksAsync_Throws_WhenEmbeddingDimensionDoesNotMatch()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();

        var wrongEmbedding = new float[TestConstants.EmbeddingDimension - 1];
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.StoreChunksAsync([
                new ChunkRecord
                {
                    Id = "chunk-1",
                    SymbolId = "symbol-1",
                    FilePath = "/src/Test.cs",
                    Content = "content",
                    Language = "CSharp",
                    Embedding = wrongEmbedding.AsMemory()
                }
            ]));

        Assert.That(ex!.Message, Does.Contain("embedding dimension"));

        Cleanup(tempDir);
    }

    [Test]
    public async Task ClearAllAsync_DropsDataAndRequiresReinitialization()
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
            }
        ]);

        await storage.ClearAllAsync();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.GetSymbolAsync("symbol-1"));
        Assert.That(ex!.Message, Does.Contain("not initialized"));

        await storage.InitializeAsync();
        var symbol = await storage.GetSymbolAsync("symbol-1");
        Assert.That(symbol, Is.Null);

        Cleanup(tempDir);
    }

    static HybridStorageService CreateStorage(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "CodeMemoryHybridTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var dbPath = Path.Combine(tempDir, "hybrid.db");
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
