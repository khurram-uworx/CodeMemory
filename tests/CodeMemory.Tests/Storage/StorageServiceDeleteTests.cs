using CodeMemory.Storage;
using Memori.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Storage;

public sealed class StorageServiceDeleteTests
{
    static StorageService CreateInMemoryStorage()
    {
        var store = new InMemoryVectorStore();
        var repoRoot = Path.Combine(Path.GetTempPath(), "CodeMemoryDeleteTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(repoRoot);
        return new StorageService(repoRoot, NullLogger<StorageService>.Instance, store);
    }

    [Test]
    public async Task DeleteSymbolsByFileAsync_RemovesCorrectRecords()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "s1", Name = "S1", Kind = "Class", FilePath = "src/A.cs", LineStart = 1, LineEnd = 10, FullName = "S1" },
            new SymbolRecord { Id = "s2", Name = "S2", Kind = "Method", FilePath = "src/B.cs", LineStart = 1, LineEnd = 10, FullName = "S2" },
            new SymbolRecord { Id = "s3", Name = "S3", Kind = "Class", FilePath = "src/A.cs", LineStart = 20, LineEnd = 30, FullName = "S3" },
        ]);

        await storage.DeleteSymbolsByFileAsync("src/A.cs");

        Assert.That(await storage.GetSymbolsByFileAsync("src/A.cs"), Is.Empty);
        var other = await storage.GetSymbolsByFileAsync("src/B.cs");
        Assert.That(other, Has.Count.EqualTo(1));
        Assert.That(other[0].Id, Is.EqualTo("s2"));
    }

    [Test]
    public async Task DeleteSymbolsByFileAsync_NonExistentFile_IsNoOp()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "s1", Name = "S1", Kind = "Class", FilePath = "src/A.cs", LineStart = 1, LineEnd = 10, FullName = "S1" },
        ]);

        Assert.DoesNotThrowAsync(async () =>
            await storage.DeleteSymbolsByFileAsync("nonexistent.cs"));

        Assert.That(await storage.GetSymbolsByFileAsync("src/A.cs"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DeleteChunksByFileAsync_RemovesCorrectRecords()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        var embedding = new float[TestConstants.EmbeddingDimension];

        await storage.StoreChunksAsync([
            new ChunkRecord { Id = "c1", SymbolId = "S1", FilePath = "src/A.cs", Content = "chunk1", Language = "CSharp", LineStart = 1, LineEnd = 10, Embedding = embedding.AsMemory() },
            new ChunkRecord { Id = "c2", SymbolId = "S2", FilePath = "src/B.cs", Content = "chunk2", Language = "CSharp", LineStart = 1, LineEnd = 10, Embedding = embedding.AsMemory() },
            new ChunkRecord { Id = "c3", SymbolId = "S3", FilePath = "src/A.cs", Content = "chunk3", Language = "CSharp", LineStart = 20, LineEnd = 30, Embedding = embedding.AsMemory() },
        ]);

        await storage.DeleteChunksByFileAsync("src/A.cs");

        Assert.That(await storage.GetChunkAsync("c1"), Is.Null);
        Assert.That(await storage.GetChunkAsync("c3"), Is.Null);
        Assert.That(await storage.GetChunkAsync("c2"), Is.Not.Null);
    }

    [Test]
    public async Task DeleteChunksByFileAsync_NonExistentFile_IsNoOp()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        var embedding = new float[TestConstants.EmbeddingDimension];
        await storage.StoreChunksAsync([
            new ChunkRecord { Id = "c1", SymbolId = "S1", FilePath = "src/A.cs", Content = "chunk1", Language = "CSharp", LineStart = 1, LineEnd = 10, Embedding = embedding.AsMemory() },
        ]);

        Assert.DoesNotThrowAsync(async () =>
            await storage.DeleteChunksByFileAsync("nonexistent.cs"));

        Assert.That(await storage.GetChunkAsync("c1"), Is.Not.Null);
    }

    [Test]
    public async Task DeleteRelationshipsBySourceIdsAsync_RemovesCorrectRecords()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "A", TargetSymbolId = "C", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r3", SourceSymbolId = "B", TargetSymbolId = "C", RelationshipType = "References" },
        ]);

        await storage.DeleteRelationshipsBySourceIdsAsync(["A"]);

        Assert.That(await storage.GetRelationshipAsync("r1"), Is.Null);
        Assert.That(await storage.GetRelationshipAsync("r2"), Is.Null);
        Assert.That(await storage.GetRelationshipAsync("r3"), Is.Not.Null);
    }

    [Test]
    public async Task DeleteRelationshipsBySourceIdsAsync_EmptyIds_IsNoOp()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Uses" },
        ]);

        Assert.DoesNotThrowAsync(async () =>
            await storage.DeleteRelationshipsBySourceIdsAsync([]));

        Assert.That(await storage.GetRelationshipAsync("r1"), Is.Not.Null);
    }

    [Test]
    public async Task DeleteRelationshipsByTargetIdsAsync_RemovesCorrectRecords()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "C", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "B", TargetSymbolId = "C", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r3", SourceSymbolId = "A", TargetSymbolId = "D", RelationshipType = "References" },
        ]);

        await storage.DeleteRelationshipsByTargetIdsAsync(["C"]);

        Assert.That(await storage.GetRelationshipAsync("r1"), Is.Null);
        Assert.That(await storage.GetRelationshipAsync("r2"), Is.Null);
        Assert.That(await storage.GetRelationshipAsync("r3"), Is.Not.Null);
    }

    [Test]
    public async Task DeleteRelationshipsByTargetIdsAsync_EmptyIds_IsNoOp()
    {
        var storage = CreateInMemoryStorage();
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Uses" },
        ]);

        Assert.DoesNotThrowAsync(async () =>
            await storage.DeleteRelationshipsByTargetIdsAsync([]));

        Assert.That(await storage.GetRelationshipAsync("r1"), Is.Not.Null);
    }

    [Test]
    public async Task DeleteMethods_Throw_WhenNotInitialized()
    {
        var storage = CreateInMemoryStorage();

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await storage.DeleteSymbolsByFileAsync("test.cs"));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await storage.DeleteChunksByFileAsync("test.cs"));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await storage.DeleteRelationshipsBySourceIdsAsync(["A"]));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await storage.DeleteRelationshipsByTargetIdsAsync(["A"]));
        });
    }

    [TearDown]
    public void TearDown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryDeleteTests");
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
