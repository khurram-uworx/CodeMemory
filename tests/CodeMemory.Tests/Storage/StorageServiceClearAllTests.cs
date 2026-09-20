using CodeMemory.Storage;
using Memori.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Storage;

public sealed class StorageServiceClearAllTests
{
    static StorageService CreateInMemoryStorage()
    {
        var store = new InMemoriVectorStore();
        var repoRoot = Path.Combine(Path.GetTempPath(), "CodeMemoryClearAllTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(repoRoot);
        return new StorageService(repoRoot, NullLogger<StorageService>.Instance, store);
    }

    [Test]
    public async Task ClearAllAsync_WithInMemoryStore_EmptiesAllCollections()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "s1", Name = "S1", Kind = "Class", FilePath = "src/A.cs", LineStart = 1, LineEnd = 10, FullName = "S1" },
        ]);
        await storage.StoreChunksAsync([
            new ChunkRecord
            {
                Id = "chunk1", SymbolId = "s1", FilePath = "src/A.cs", Content = "content",
                Language = "CSharp", LineStart = 1, LineEnd = 10,
                Embedding = new float[TestConstants.EmbeddingDimension].AsMemory(),
            },
        ]);
        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "rel1", SourceSymbolId = "s1", TargetSymbolId = "s2", RelationshipType = "References" },
        ]);

        await storage.ClearAllAsync();
        await storage.InitializeAsync();

        Assert.That(await storage.GetSymbolAsync("s1"), Is.Null);
        Assert.That(await storage.GetSymbolsByFileAsync("src/A.cs"), Is.Empty);
        Assert.That(await storage.GetChunkAsync("chunk1"), Is.Null);
        Assert.That(await storage.GetRelationshipAsync("rel1"), Is.Null);
        Assert.That(await storage.GetSymbolByFullNameAsync("S1"), Is.Null);
    }

    [Test]
    public async Task ClearAllAsync_ThenRestoreSameFile_DoesNotDuplicateRows()
    {
        // Mirrors rescan_repository: clear, re-initialize, re-crawl with new GUIDs (same file paths).
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "old1", Name = "S1", Kind = "Class", FilePath = "src/A.cs", LineStart = 1, LineEnd = 10, FullName = "S1" },
        ]);
        await storage.ClearAllAsync();
        await storage.InitializeAsync();

        // New crawl generates a fresh GUID for the same file.
        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "new1", Name = "S1", Kind = "Class", FilePath = "src/A.cs", LineStart = 1, LineEnd = 10, FullName = "S1" },
        ]);

        var symbols = await storage.GetSymbolsByFileAsync("src/A.cs");
        Assert.That(symbols, Has.Count.EqualTo(1));
        Assert.That(symbols[0].Id, Is.EqualTo("new1"));
    }

    [Test]
    public async Task ClearAllAsync_WhenNotInitialized_Throws()
    {
        var storage = CreateInMemoryStorage();
        Assert.ThrowsAsync<InvalidOperationException>(() => storage.ClearAllAsync());
    }
}