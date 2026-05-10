using CodeMemory.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CodeMemory.Tests.Storage;

public sealed class StorageServiceTests
{
    static string getTempDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    static IStorageService createStorageService(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddCodeMemoryStorage($"Data Source={dbPath}");
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IStorageService>();
    }

    [Test]
    public async Task InitializeAsync_CreatesDatabaseFile()
    {
        var dbPath = getTempDbPath();
        Assert.That(File.Exists(dbPath), Is.False);

        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        Assert.That(File.Exists(dbPath), Is.True);
        Assert.That(new FileInfo(dbPath).Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task StoreSymbolsAsync_And_GetSymbolAsync_RoundTrip()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var symbol = new SymbolRecord
        {
            Id = "TestClass",
            Name = "TestClass",
            Kind = "Class",
            FilePath = "/src/Test.cs",
            LineStart = 1,
            LineEnd = 50,
            FullName = "TestClass",
            Modifiers = "public",
            Documentation = "A test class"
        };

        await storage.StoreSymbolsAsync([symbol]);

        var retrieved = await storage.GetSymbolAsync("TestClass");
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.Name, Is.EqualTo("TestClass"));
        Assert.That(retrieved.Kind, Is.EqualTo("Class"));
        Assert.That(retrieved.FilePath, Is.EqualTo("/src/Test.cs"));
        Assert.That(retrieved.LineStart, Is.EqualTo(1));
        Assert.That(retrieved.LineEnd, Is.EqualTo(50));
        Assert.That(retrieved.FullName, Is.EqualTo("TestClass"));
        Assert.That(retrieved.Modifiers, Is.EqualTo("public"));
        Assert.That(retrieved.Documentation, Is.EqualTo("A test class"));
    }

    [Test]
    public async Task StoreChunksAsync_And_GetChunkAsync_RoundTrip()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var embedding = new float[TestConstants.EmbeddingDimension];

        var chunk = new ChunkRecord
        {
            Id = "chunk1",
            SymbolId = "TestClass",
            FilePath = "/src/Test.cs",
            Content = "public class TestClass { }",
            Language = "CSharp",
            LineStart = 1,
            LineEnd = 10,
            MetadataJson = """{"key":"value"}""",
            Embedding = embedding.AsMemory()
        };

        await storage.StoreChunksAsync([chunk]);

        var retrieved = await storage.GetChunkAsync("chunk1");
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.SymbolId, Is.EqualTo("TestClass"));
        Assert.That(retrieved.Content, Is.EqualTo("public class TestClass { }"));
        Assert.That(retrieved.Language, Is.EqualTo("CSharp"));
        Assert.That(retrieved.MetadataJson, Is.EqualTo("""{"key":"value"}"""));
        Assert.That(retrieved.Embedding, Is.Not.Null);
        Assert.That(retrieved.Embedding.Value.Length, Is.EqualTo(TestConstants.EmbeddingDimension));
    }

    [Test]
    public async Task StoreChunksAsync_WithEmbedding_PersistsVector()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var embedding = new float[TestConstants.EmbeddingDimension];
        for (int i = 0; i < embedding.Length; i++)
            embedding[i] = i / (float)TestConstants.EmbeddingDimension;

        var chunk = new ChunkRecord
        {
            Id = "chunk_vec",
            SymbolId = "TestClass",
            FilePath = "/src/Test.cs",
            Content = "public class TestClass { }",
            Language = "CSharp",
            LineStart = 1,
            LineEnd = 10,
            Embedding = embedding.AsMemory()
        };

        await storage.StoreChunksAsync([chunk]);

        var retrieved = await storage.GetChunkAsync("chunk_vec");
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.Embedding, Is.Not.Null);
        Assert.That(retrieved.Embedding.Value.Length, Is.EqualTo(TestConstants.EmbeddingDimension));
        Assert.That(retrieved.Embedding.Value.Span[0], Is.EqualTo(0f).Within(0.001f));
        Assert.That(retrieved.Embedding.Value.Span[100], Is.EqualTo(100f / (float)TestConstants.EmbeddingDimension).Within(0.001f));
    }

    [Test]
    public async Task StoreRelationshipsAsync_And_GetRelationshipAsync_RoundTrip()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var relationship = new RelationshipRecord
        {
            Id = "rel1",
            SourceSymbolId = "ClassA",
            TargetSymbolId = "ClassB",
            RelationshipType = "Uses"
        };

        await storage.StoreRelationshipsAsync([relationship]);

        var retrieved = await storage.GetRelationshipAsync("rel1");
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.SourceSymbolId, Is.EqualTo("ClassA"));
        Assert.That(retrieved.TargetSymbolId, Is.EqualTo("ClassB"));
        Assert.That(retrieved.RelationshipType, Is.EqualTo("Uses"));
    }

    [Test]
    public async Task GetSymbolAsync_ReturnsNull_ForMissingKey()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var result = await storage.GetSymbolAsync("nonexistent");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetChunkAsync_ReturnsNull_ForMissingKey()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var result = await storage.GetChunkAsync("nonexistent");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetRelationshipAsync_ReturnsNull_ForMissingKey()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var result = await storage.GetRelationshipAsync("nonexistent");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task StoreSymbolsAsync_AccumulatesData_AcrossMultipleCalls()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "sym1", Name = "Sym1", Kind = "Class",
                FilePath = "/src/Test.cs", LineStart = 1, LineEnd = 10,
                FullName = "Sym1"
            }
        ]);

        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "sym2", Name = "Sym2", Kind = "Method",
                FilePath = "/src/Test.cs", LineStart = 20, LineEnd = 30,
                FullName = "Sym2"
            }
        ]);

        var sym1 = await storage.GetSymbolAsync("sym1");
        var sym2 = await storage.GetSymbolAsync("sym2");

        Assert.That(sym1, Is.Not.Null);
        Assert.That(sym2, Is.Not.Null);
        Assert.That(sym1!.Name, Is.EqualTo("Sym1"));
        Assert.That(sym2!.Name, Is.EqualTo("Sym2"));
    }

    [Test]
    public async Task StoreSymbolsAsync_Throws_WhenNotInitialized()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.StoreSymbolsAsync([]));
        Assert.That(ex!.Message, Does.Contain("not initialized"));
    }

    [Test]
    public async Task StoreChunksAsync_Throws_WhenNotInitialized()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.StoreChunksAsync([]));
        Assert.That(ex!.Message, Does.Contain("not initialized"));
    }

    [Test]
    public async Task StoreRelationshipsAsync_Throws_WhenNotInitialized()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.StoreRelationshipsAsync([]));
        Assert.That(ex!.Message, Does.Contain("not initialized"));
    }

    [Test]
    public async Task GetSymbolAsync_Throws_WhenNotInitialized()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.GetSymbolAsync("test"));
        Assert.That(ex!.Message, Does.Contain("not initialized"));
    }

    [Test]
    public async Task GetChunkAsync_Throws_WhenNotInitialized()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.GetChunkAsync("test"));
        Assert.That(ex!.Message, Does.Contain("not initialized"));
    }

    [Test]
    public async Task GetRelationshipAsync_Throws_WhenNotInitialized()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await storage.GetRelationshipAsync("test"));
        Assert.That(ex!.Message, Does.Contain("not initialized"));
    }

    [Test]
    public async Task DataSurvivesAcrossInstances()
    {
        var dbPath = getTempDbPath();

        // First instance: store data
        var storage1 = createStorageService(dbPath);
        await storage1.InitializeAsync();
        await storage1.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "persistent", Name = "Persistent", Kind = "Class",
                FilePath = "/src/Test.cs", LineStart = 1, LineEnd = 10,
                FullName = "Persistent"
            }
        ]);
        await storage1.StoreChunksAsync([
            new ChunkRecord
            {
                Id = "persistent_chunk", SymbolId = "persistent",
                FilePath = "/src/Test.cs", Content = "persistent content",
                Language = "CSharp", LineStart = 1, LineEnd = 10,
                Embedding = new float[TestConstants.EmbeddingDimension].AsMemory()
            }
        ]);
        await storage1.StoreRelationshipsAsync([
            new RelationshipRecord
            {
                Id = "persistent_rel", SourceSymbolId = "A",
                TargetSymbolId = "B", RelationshipType = "References"
            }
        ]);

        // Second instance: read data from same file
        var storage2 = createStorageService(dbPath);
        await storage2.InitializeAsync();

        var symbol = await storage2.GetSymbolAsync("persistent");
        Assert.That(symbol, Is.Not.Null);
        Assert.That(symbol!.Name, Is.EqualTo("Persistent"));

        var chunk = await storage2.GetChunkAsync("persistent_chunk");
        Assert.That(chunk, Is.Not.Null);
        Assert.That(chunk!.Content, Is.EqualTo("persistent content"));

        var rel = await storage2.GetRelationshipAsync("persistent_rel");
        Assert.That(rel, Is.Not.Null);
        Assert.That(rel!.RelationshipType, Is.EqualTo("References"));
    }

    [Test]
    public async Task GetSymbolsByFileAsync_ReturnsMatchingSymbols()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "s1", Name = "S1", Kind = "Class",
                FilePath = "/src/A.cs", LineStart = 1, LineEnd = 10, FullName = "S1" },
            new SymbolRecord { Id = "s2", Name = "S2", Kind = "Method",
                FilePath = "/src/B.cs", LineStart = 1, LineEnd = 10, FullName = "S2" },
            new SymbolRecord { Id = "s3", Name = "S3", Kind = "Class",
                FilePath = "/src/A.cs", LineStart = 20, LineEnd = 30, FullName = "S3" }
        ]);

        var results = await storage.GetSymbolsByFileAsync("/src/A.cs");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["s1", "s3"]));
    }

    [Test]
    public async Task GetSymbolsByFileAsync_ReturnsEmpty_WhenNoMatch()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var results = await storage.GetSymbolsByFileAsync("/nonexistent.cs");
        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetSymbolsByKindAsync_ReturnsMatchingSymbols()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord { Id = "s1", Name = "S1", Kind = "Class",
                FilePath = "/src/A.cs", LineStart = 1, LineEnd = 10, FullName = "S1" },
            new SymbolRecord { Id = "s2", Name = "S2", Kind = "Method",
                FilePath = "/src/B.cs", LineStart = 1, LineEnd = 10, FullName = "S2" }
        ]);

        var results = await storage.GetSymbolsByKindAsync("Method");
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Id, Is.EqualTo("s2"));
    }

    [Test]
    public async Task GetChunksBySymbolAsync_ReturnsChunksForSymbol()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        await storage.StoreChunksAsync([
            new ChunkRecord { Id = "c1", SymbolId = "A", FilePath = "/src/A.cs",
                Content = "chunk1", Language = "CSharp", LineStart = 1, LineEnd = 10,
                Embedding = new float[TestConstants.EmbeddingDimension].AsMemory() },
            new ChunkRecord { Id = "c2", SymbolId = "B", FilePath = "/src/B.cs",
                Content = "chunk2", Language = "CSharp", LineStart = 1, LineEnd = 10,
                Embedding = new float[TestConstants.EmbeddingDimension].AsMemory() },
            new ChunkRecord { Id = "c3", SymbolId = "A", FilePath = "/src/A.cs",
                Content = "chunk3", Language = "CSharp", LineStart = 20, LineEnd = 30,
                Embedding = new float[TestConstants.EmbeddingDimension].AsMemory() }
        ]);

        var results = await storage.GetChunksBySymbolAsync("A");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["c1", "c3"]));
    }

    [Test]
    public async Task GetRelationshipsBySourceAsync_ReturnsMatching()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "A", TargetSymbolId = "C", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r3", SourceSymbolId = "B", TargetSymbolId = "C", RelationshipType = "References" }
        ]);

        var results = await storage.GetRelationshipsBySourceAsync("A");
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(["r1", "r2"]));
    }

    [Test]
    public async Task GetRelationshipsByTargetAsync_ReturnsMatching()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new RelationshipRecord { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "C", RelationshipType = "Uses" },
            new RelationshipRecord { Id = "r2", SourceSymbolId = "B", TargetSymbolId = "C", RelationshipType = "References" }
        ]);

        var results = await storage.GetRelationshipsByTargetAsync("C");
        Assert.That(results, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task SearchChunksAsync_ReturnsResultsOrderedByScore()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var near = new float[TestConstants.EmbeddingDimension];
        near[0] = 1.0f;
        var far = new float[TestConstants.EmbeddingDimension];
        far[0] = -1.0f;

        await storage.StoreChunksAsync([
            new ChunkRecord { Id = "near", SymbolId = "A", FilePath = "/src/A.cs",
                Content = "near match", Language = "CSharp", LineStart = 1, LineEnd = 10,
                Embedding = near.AsMemory() },
            new ChunkRecord { Id = "far", SymbolId = "B", FilePath = "/src/B.cs",
                Content = "far match", Language = "CSharp", LineStart = 1, LineEnd = 10,
                Embedding = far.AsMemory() }
        ]);

        var query = new float[TestConstants.EmbeddingDimension];
        query[0] = 0.9f;

        var results = await storage.SearchChunksAsync(query.AsMemory(), top: 5);

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Chunk.Id, Is.EqualTo("near"));
        Assert.That(results[0].Score, Is.LessThan(results[1].Score));
        Assert.That(results[0].Chunk.Content, Is.EqualTo("near match"));
    }

    [Test]
    public async Task SearchChunksAsync_RespectsTopLimit()
    {
        var dbPath = getTempDbPath();
        var storage = createStorageService(dbPath);
        await storage.InitializeAsync();

        var chunks = new List<ChunkRecord>();
        for (int i = 0; i < 5; i++)
        {
            var v = new float[TestConstants.EmbeddingDimension];
            v[0] = i / 10f;
            chunks.Add(new ChunkRecord
            {
                Id = $"c{i}",
                SymbolId = $"S{i}",
                FilePath = $"/src/{i}.cs",
                Content = $"chunk {i}",
                Language = "CSharp",
                LineStart = 1,
                LineEnd = 10,
                Embedding = v.AsMemory()
            });
        }
        await storage.StoreChunksAsync(chunks);

        var query = new float[TestConstants.EmbeddingDimension];
        var results = await storage.SearchChunksAsync(query.AsMemory(), top: 3);
        Assert.That(results, Has.Count.EqualTo(3));
    }

    [TearDown]
    public void TearDown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryTests");
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
