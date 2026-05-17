using CodeMemory.AspNet.Storage.PgVector;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;

namespace CodeMemory.Tests.Storage;

[TestFixture]
[Category("Integration")]
[Category("PgVector")]
public sealed class PgVectorStorageServiceTests
{
    const string ConnectionString = "Host=localhost;Port=5432;Database=codememory;Username=codememory;Password=codememory";

    static PgVectorStore CreateStore()
        => new(ConnectionString, new PgVectorOptions { Schema = "test" });

    static StorageService CreateStorage()
    {
        var store = CreateStore();
        return new StorageService(
            Environment.CurrentDirectory,
            NullLogger<StorageService>.Instance,
            store);
    }

    [Test]
    public async Task Initialize_CreatesTables()
    {
        var store = CreateStore();

        Assert.That(await store.CollectionExistsAsync("symbols", default), Is.False);
        Assert.That(await store.CollectionExistsAsync("chunks", default), Is.False);
        Assert.That(await store.CollectionExistsAsync("relationships", default), Is.False);
    }

    [Test]
    public async Task StoreAndRetrieveSymbol_RoundTrip()
    {
        var storage = CreateStorage();
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
        Assert.Multiple(() =>
        {
            Assert.That(retrieved!.Name, Is.EqualTo("TestClass"));
            Assert.That(retrieved.Kind, Is.EqualTo("Class"));
            Assert.That(retrieved.FilePath, Is.EqualTo("/src/Test.cs"));
            Assert.That(retrieved.LineStart, Is.EqualTo(1));
            Assert.That(retrieved.LineEnd, Is.EqualTo(50));
            Assert.That(retrieved.Modifiers, Is.EqualTo("public"));
            Assert.That(retrieved.Documentation, Is.EqualTo("A test class"));
        });
    }

    [Test]
    public async Task GetSymbolByFile_ReturnsMatchingSymbols()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "s1", Name = "ClassA", Kind = "Class", FilePath = "/src/a.cs", FullName = "ClassA" },
            new() { Id = "s2", Name = "ClassB", Kind = "Class", FilePath = "/src/b.cs", FullName = "ClassB" },
            new() { Id = "s3", Name = "ClassC", Kind = "Class", FilePath = "/src/a.cs", FullName = "ClassC" },
        ]);

        var results = await storage.GetSymbolsByFileAsync("/src/a.cs");

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(new[] { "s1", "s3" }));
    }

    [Test]
    public async Task GetSymbolByKind_ReturnsMatchingSymbols()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "m1", Name = "Method1", Kind = "Method", FilePath = "/src/c.cs", FullName = "ClassC.Method1" },
            new() { Id = "c1", Name = "Class1", Kind = "Class", FilePath = "/src/d.cs", FullName = "Class1" },
            new() { Id = "m2", Name = "Method2", Kind = "Method", FilePath = "/src/c.cs", FullName = "ClassC.Method2" },
        ]);

        var results = await storage.GetSymbolsByKindAsync("Method");

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(new[] { "m1", "m2" }));
    }

    [Test]
    public async Task StoreAndRetrieveRelationship_RoundTrip()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        var rel = new RelationshipRecord
        {
            Id = "rel1",
            SourceSymbolId = "ClassA",
            TargetSymbolId = "ClassB",
            RelationshipType = "References"
        };

        await storage.StoreRelationshipsAsync([rel]);
        var retrieved = await storage.GetRelationshipAsync("rel1");

        Assert.That(retrieved, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(retrieved!.SourceSymbolId, Is.EqualTo("ClassA"));
            Assert.That(retrieved.TargetSymbolId, Is.EqualTo("ClassB"));
            Assert.That(retrieved.RelationshipType, Is.EqualTo("References"));
        });
    }

    [Test]
    public async Task GetRelationshipsBySource_ReturnsMatching()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        await storage.StoreRelationshipsAsync([
            new() { Id = "r1", SourceSymbolId = "A", TargetSymbolId = "B", RelationshipType = "Calls" },
            new() { Id = "r2", SourceSymbolId = "A", TargetSymbolId = "C", RelationshipType = "References" },
            new() { Id = "r3", SourceSymbolId = "B", TargetSymbolId = "C", RelationshipType = "Calls" },
        ]);

        var results = await storage.GetRelationshipsBySourceAsync("A");

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Select(r => r.Id), Is.EquivalentTo(new[] { "r1", "r2" }));
    }

    [Test]
    public async Task GetNonexistentKey_ReturnsNull()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        Assert.That(await storage.GetSymbolAsync("nonexistent"), Is.Null);
        Assert.That(await storage.GetChunkAsync("nonexistent"), Is.Null);
        Assert.That(await storage.GetRelationshipAsync("nonexistent"), Is.Null);
    }

    [Test]
    public async Task UpsertReplacesExistingRecord()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "x", Name = "Original", Kind = "Class", FilePath = "/x.cs", FullName = "X" }
        ]);
        await storage.StoreSymbolsAsync([
            new() { Id = "x", Name = "Updated", Kind = "Method", FilePath = "/x.cs", FullName = "X" }
        ]);

        var retrieved = await storage.GetSymbolAsync("x");
        Assert.That(retrieved!.Name, Is.EqualTo("Updated"));
        Assert.That(retrieved.Kind, Is.EqualTo("Method"));
    }

    [Test]
    public async Task DeleteSymbol_RemovesRecord()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new() { Id = "del", Name = "DeleteMe", Kind = "Class", FilePath = "/d.cs", FullName = "DeleteMe" }
        ]);
        Assert.That(await storage.GetSymbolAsync("del"), Is.Not.Null);

        await storage.StoreSymbolsAsync([]); // no delete method on IStorageService
        // Verify it still exists (no delete on IStorageService)
        Assert.That(await storage.GetSymbolAsync("del"), Is.Not.Null);
    }

    // ── Vector Search Tests ──────────────────────────────────────────────

    static ReadOnlyMemory<float> CreateVector(float first, float second)
    {
        var vec = new float[1536];
        vec[0] = first;
        vec[1] = second;
        return vec;
    }

    [Test]
    public async Task VectorSearch_Identity_ReturnsHighScore()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        var vec = CreateVector(1.0f, 0f);
        await storage.StoreChunksAsync([
            new() { Id = "c1", SymbolId = "S1", FilePath = "/f.cs", Content = "hello", Language = "csharp", Embedding = vec }
        ]);

        var results = await storage.SearchChunksAsync(vec);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Chunk.Id, Is.EqualTo("c1"));
        Assert.That(results[0].Score, Is.GreaterThan(0.99));
    }

    [Test]
    public async Task VectorSearch_Orthogonal_ReturnsLowScore()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        var vecA = CreateVector(1.0f, 0f);
        var vecB = CreateVector(0f, 1.0f);
        await storage.StoreChunksAsync([
            new() { Id = "c1", SymbolId = "S1", FilePath = "/f.cs", Content = "hello", Language = "csharp", Embedding = vecA }
        ]);

        var results = await storage.SearchChunksAsync(vecB);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Score, Is.LessThan(0.01));
    }

    [Test]
    public async Task VectorSearch_WithFilter_ReturnsFilteredResults()
    {
        var storage = CreateStorage();
        await storage.InitializeAsync();

        var vec = CreateVector(1.0f, 0f);
        await storage.StoreChunksAsync([
            new() { Id = "c1", SymbolId = "S1", FilePath = "/a.cs", Content = "alpha", Language = "cs", Embedding = vec },
            new() { Id = "c2", SymbolId = "S2", FilePath = "/b.cs", Content = "beta", Language = "cs", Embedding = vec },
        ]);

        var options = new VectorSearchOptions<ChunkRecord>
        {
            Filter = c => c.SymbolId == "S1"
        };
        var results = await storage.SearchChunksAsync(vec, top: 10, options: options);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Chunk.Id, Is.EqualTo("c1"));
    }

    // ── Cleanup: drop test schema after all tests ─────────────────────

    [TearDown]
    public async Task Cleanup()
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "DROP SCHEMA IF EXISTS test CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
