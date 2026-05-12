using CodeMemory.Indexing.Architecture;
using CodeMemory.Indexing.Git;
using CodeMemory.Indexing.Graph;
using CodeMemory.Indexing.Search;
using CodeMemory.Storage;

namespace CodeMemory.Tests;

sealed class MockStorageService : IStorageService
{
    public string RepoRoot => Environment.CurrentDirectory;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task StoreChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord> relationships, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken ct = default)
    {
        if (id == "MyClass")
            return Task.FromResult<SymbolRecord?>(new SymbolRecord
            {
                Id = "MyClass",
                Name = "MyClass",
                Kind = "Class",
                FilePath = "/src/MyClass.cs",
                FullName = "MyClass",
                LineStart = 1,
                LineEnd = 50
            });
        return Task.FromResult<SymbolRecord?>(null);
    }

    public Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken ct = default)
        => Task.FromResult<ChunkRecord?>(null);

    public Task<RelationshipRecord?> GetRelationshipAsync(string id, CancellationToken ct = default)
        => Task.FromResult<RelationshipRecord?>(null);

    public Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(string filePath, int top = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SymbolRecord>>([]);

    public Task<IReadOnlyList<SymbolRecord>> GetSymbolsByKindAsync(string kind, int top = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SymbolRecord>>([]);

    public Task<IReadOnlyList<ChunkRecord>> GetChunksBySymbolAsync(string symbolId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ChunkRecord>>([
            new ChunkRecord
                {
                    Id = "c1", SymbolId = "MyClass", FilePath = "/src/MyClass.cs",
                    Content = "public class MyClass { }", Language = "CSharp",
                    LineStart = 1, LineEnd = 10
                }
        ]);
    }

    public Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsBySourceAsync(string sourceSymbolId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RelationshipRecord>>([]);

    public Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsByTargetAsync(string targetSymbolId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RelationshipRecord>>([]);

    public Task<IReadOnlyList<ScoredChunk>> SearchChunksAsync(ReadOnlyMemory<float> embedding, int top = 10, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScoredChunk>>([]);

    public Task ClearAllAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}

sealed class MockGraphService : IDependencyGraphService
{
    public Task<IReadOnlyList<DependencyNode>> TraceAsync(
        string symbolPath, string direction, int depth, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DependencyNode>>([
            new("MyOtherClass", "/src/Other.cs", "Class", "1-30", "references")
        ]);

    public Task<IReadOnlyList<DependencyNode>> FindRelatedAsync(
        string symbolPath, string relationType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DependencyNode>>([]);

    public Task<IReadOnlyList<string>> FindTestCoverageAsync(
        string symbolPath, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(["/tests/MyClassTest.cs"]);
}

sealed class MockSemanticSearchService : ISemanticSearchService
{
    public Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        string query, int maxResults = 10, double minimumSimilarity = 0, CancellationToken ct = default)
    {
        var results = new List<ScoredChunk>
            {
                new()
                {
                    Chunk = new ChunkRecord
                    {
                        Id = "chunk1",
                        SymbolId = "DatabaseService",
                        FilePath = "/src/DatabaseService.cs",
                        Content = "public class DatabaseService { }",
                        Language = "CSharp",
                        LineStart = 1,
                        LineEnd = 10
                    },
                    Score = 0.95
                }
            };
        return Task.FromResult<IReadOnlyList<ScoredChunk>>(results);
    }
}

sealed class MockDependencyGraphService : IDependencyGraphService
{
    public Task<IReadOnlyList<DependencyNode>> TraceAsync(
        string symbolPath, string direction, int depth, CancellationToken ct = default)
    {
        if (symbolPath == "NonExistent")
            return Task.FromResult<IReadOnlyList<DependencyNode>>([]);

        var chain = new List<DependencyNode>
            {
                new(symbolPath, "/src/MyClass.cs", "Method", "10-30", "self"),
                new("MyOtherClass", "/src/Other.cs", "Class", "1-50", direction == "upstream" ? "imports" : "references")
            };
        return Task.FromResult<IReadOnlyList<DependencyNode>>(chain);
    }

    public Task<IReadOnlyList<DependencyNode>> FindRelatedAsync(
        string symbolPath, string relationType, CancellationToken ct = default)
    {
        if (symbolPath == "NonExistent")
            return Task.FromResult<IReadOnlyList<DependencyNode>>([]);

        return Task.FromResult<IReadOnlyList<DependencyNode>>([
            new("RelatedService", "/src/Related.cs", "Class", "1-20", "references")
        ]);
    }

    public Task<IReadOnlyList<string>> FindTestCoverageAsync(
        string symbolPath, CancellationToken ct = default)
    {
        if (symbolPath == "NonExistent")
            return Task.FromResult<IReadOnlyList<string>>([]);

        return Task.FromResult<IReadOnlyList<string>>(["/tests/MyClassTest.cs"]);
    }
}

sealed class MockClusteringService : IComponentClusteringService
{
    public Task<IReadOnlyList<ComponentCluster>> GetClustersAsync(
        double threshold = 0.3, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ComponentCluster>>([
            new ComponentCluster("src+tests", ["src", "tests"], 0.75),
                new ComponentCluster("lib", ["lib"], 1.0)
        ]);
    }
}

sealed class MockGitHistoryService : IGitHistoryService
{
    public Task<SymbolHistoryResult?> GetSymbolHistoryAsync(
        string symbolPath, int maxCommits = 20, CancellationToken ct = default)
    {
        return Task.FromResult<SymbolHistoryResult?>(new SymbolHistoryResult(
            symbolPath, "/src/MyClass.cs", 3, 1,
            "2024-01-01", "2024-03-15",
            [
                new CommitInfo("abc123", "testuser", "2024-03-15", "Fix bug"),
                    new CommitInfo("def456", "testuser", "2024-02-01", "Add feature"),
            ]));
    }

    public Task<IReadOnlyList<HotspotInfo>> GetHotspotsAsync(
        int top = 10, int maxCommits = 100, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<HotspotInfo>>([
            new HotspotInfo("/src/Service.cs", 15, 3, "2024-03-15"),
                new HotspotInfo("/src/Controller.cs", 8, 2, "2024-03-10"),
            ]);
    }
}

sealed class MockArchitectureService : IArchitectureService
{
    public Task<ArchitectureOverview> GetOverviewAsync(string? path = null, CancellationToken ct = default)
    {
        return Task.FromResult(new ArchitectureOverview(
            [new ComponentInfo("src", 5, 20), new ComponentInfo("tests", 3, 2)],
            new Dictionary<string, int> { ["C#"] = 8, ["JavaScript"] = 2 },
            10, 42
        ));
    }
}
