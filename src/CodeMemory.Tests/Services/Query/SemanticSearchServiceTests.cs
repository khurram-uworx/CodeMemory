using CodeMemory.Services.Query;
using CodeMemory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Query;

public sealed class SemanticSearchServiceTests
{
    static string getTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    static (IStorageService Storage, SemanticSearchService Service) createServices(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddCodeMemoryStorage($"Data Source={dbPath}");
        var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IStorageService>();
        var svc = new SemanticSearchService(storage, NullLogger<SemanticSearchService>.Instance);
        return (storage, svc);
    }

    [Test]
    public async Task SearchByTextAsync_ReturnsEmpty_WhenNoEmbeddingGenerator()
    {
        var dbPath = getTempDb();
        var (storage, svc) = createServices(dbPath);
        await storage.InitializeAsync();

        var results = await svc.SearchByTextAsync("some query");
        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task SearchByVectorAsync_ReturnsResults_OrderedByScore()
    {
        var dbPath = getTempDb();
        var (storage, svc) = createServices(dbPath);
        await storage.InitializeAsync();

        // Store two chunks with different embeddings
        var vec1 = new float[TestConstants.EmbeddingDimension];
        vec1[0] = 1.0f;
        var vec2 = new float[TestConstants.EmbeddingDimension];
        vec2[0] = -1.0f;

        await storage.StoreChunksAsync([
            new ChunkRecord
            {
                Id = "match", SymbolId = "A", FilePath = "/src/A.cs",
                Content = "close match", Language = "CSharp",
                LineStart = 1, LineEnd = 10, Embedding = vec1.AsMemory()
            },
            new ChunkRecord
            {
                Id = "distant", SymbolId = "B", FilePath = "/src/B.cs",
                Content = "far match", Language = "CSharp",
                LineStart = 1, LineEnd = 10, Embedding = vec2.AsMemory()
            }
        ]);

        // Search with a vector close to vec1
        var query = new float[TestConstants.EmbeddingDimension];
        query[0] = 0.9f;

        var results = await svc.SearchByVectorAsync(query.AsMemory(), top: 5);

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Chunk.Id, Is.EqualTo("match"));
        Assert.That(results[0].Score, Is.LessThan(results[1].Score));
    }

    [Test]
    public async Task SearchByVectorAsync_RespectsTop()
    {
        var dbPath = getTempDb();
        var (storage, svc) = createServices(dbPath);
        await storage.InitializeAsync();

        var vec = new float[TestConstants.EmbeddingDimension];
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

        var results = await svc.SearchByVectorAsync(vec.AsMemory(), top: 3);
        Assert.That(results, Has.Count.EqualTo(3));
    }
}
