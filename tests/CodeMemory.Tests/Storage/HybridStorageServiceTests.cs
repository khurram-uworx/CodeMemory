using CodeMemory.AspNet.Registry;
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
    public async Task Components_RoundTripThroughRegistryStore()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();

        var components = new List<ComponentInformation>
        {
            new("src/CodeMemory", "CodeMemory", ComponentKind.MsBuild, ComponentType.Component, FileCount: 42),
            new("src/CodeMemory.AspNet", "CodeMemory.AspNet", ComponentKind.MsBuild, ComponentType.Component),
            new("tests/CodeMemory.Tests", "CodeMemory.Tests", ComponentKind.MsBuild, ComponentType.Test),
        };

        await storage.StoreComponentMappingAsync(components);

        var loaded = await storage.LoadComponentMappingAsync();

        Assert.That(loaded, Has.Count.EqualTo(3));
        Assert.That(loaded.Any(c => c.BuildFilePath == "src/CodeMemory" && c.ComponentKind == ComponentKind.MsBuild), Is.True);
        Assert.That(loaded.Any(c => c.ComponentType == ComponentType.Test), Is.True);
        Assert.That(loaded.First(c => c.BuildFilePath == "tests/CodeMemory.Tests").ComponentType, Is.EqualTo(ComponentType.Test));
        Assert.That(loaded.First(c => c.BuildFilePath == "src/CodeMemory").FileCount, Is.EqualTo(42));

        Cleanup(tempDir);
    }

    [Test]
    public async Task StoreComponentMappingAsync_AddsNewEntriesWithoutRemovingExisting()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();

        await storage.StoreComponentMappingAsync([
            new ComponentInformation("src/Lib", "Lib", ComponentKind.MsBuild, ComponentType.Component),
        ]);

        await storage.StoreComponentMappingAsync([
            new ComponentInformation("src/NewLib", "NewLib", ComponentKind.MsBuild, ComponentType.Component),
        ]);

        var loaded = await storage.LoadComponentMappingAsync();

        // Existing entry persists, new entry added — total 2
        Assert.That(loaded, Has.Count.EqualTo(2));
        Assert.That(loaded.Any(c => c.ComponentName == "Lib"), Is.True);
        Assert.That(loaded.Any(c => c.ComponentName == "NewLib"), Is.True);

        Cleanup(tempDir);
    }

    [Test]
    public async Task StoreComponentMappingAsync_PreservesUserEditsOnReIndex()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();

        // First index: detect Lib as MsBuild
        await storage.StoreComponentMappingAsync([
            new ComponentInformation("src/Lib", "Lib", ComponentKind.MsBuild, ComponentType.Component),
        ]);

        // Simulate user editing via API — bypass storage, update DB directly
        await using (var db = ((HybridStorageService)storage).CreateRegistryDbContext())
        {
            var entity = await db.Components.FirstAsync(c => c.RepositoryId == 1);
            entity.ComponentKindString = "Maven"; // user changed Kind
            entity.ComponentTypeString = "Tool";  // user changed Type
            await db.SaveChangesAsync();
        }

        // Re-index with same detection (MsBuild, Component)
        await storage.StoreComponentMappingAsync([
            new ComponentInformation("src/Lib", "Lib", ComponentKind.MsBuild, ComponentType.Component),
        ]);

        var loaded = await storage.LoadComponentMappingAsync();

        // User edits should survive — Kind is still Maven, Type is still Tool
        var lib = loaded.First(c => c.BuildFilePath == "src/Lib");
        Assert.That(lib.ComponentKind, Is.EqualTo(ComponentKind.Maven));
        Assert.That(lib.ComponentType, Is.EqualTo(ComponentType.Tool));

        Cleanup(tempDir);
    }

    [Test]
    public async Task StoreComponentMappingAsync_DoesNotReAddSoftDeletedEntries()
    {
        var storage = CreateStorage(out var tempDir);
        await storage.InitializeAsync();

        await storage.StoreComponentMappingAsync([
            new ComponentInformation("src/Lib", "Lib", ComponentKind.MsBuild, ComponentType.Component),
        ]);

        // Simulate user soft-delete via API
        await using (var db = ((HybridStorageService)storage).CreateRegistryDbContext())
        {
            var entity = await db.Components.FirstAsync(c => c.RepositoryId == 1);
            entity.IsDeleted = true;
            entity.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // Re-index with same detection
        await storage.StoreComponentMappingAsync([
            new ComponentInformation("src/Lib", "Lib", ComponentKind.MsBuild, ComponentType.Component),
        ]);

        var loaded = await storage.LoadComponentMappingAsync();

        // Soft-deleted entry should NOT reappear
        Assert.That(loaded, Has.Count.EqualTo(0));

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

        var registryDbPath = Path.Combine(tempDir, "registry.db");
        var registryConnectionString = $"Data Source={registryDbPath}";
        var registryOptions = new DbContextOptionsBuilder<RepoRegistryDbContext>()
            .UseSqlite(registryConnectionString)
            .Options;

        var registryDbFactory = new TestRepoRegistryDbContextFactory(registryOptions);

        // Seed a test repo
        using (var seedDb = registryDbFactory.CreateDbContext())
        {
            seedDb.Database.EnsureCreated();
            seedDb.RegisteredRepos.Add(new Repositories
            {
                Id = 1,
                Name = "test-repo",
                LocalPath = tempDir,
                CloneStatus = "Cloned",
                IndexStatus = "Pending"
            });
            seedDb.SaveChanges();
        }

        return new HybridStorageService(
            tempDir,
            registeredRepoId: 1,
            NullLogger<HybridStorageService>.Instance,
            store,
            () => new CodeMemoryDbContext(options, "main"),
            registryDbFactory,
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
