using CodeMemory.Indexing.Graph;
using CodeMemory.Indexing.Search;
using CodeMemory.Storage.Models;
using CodeMemory.Storage.Services;

namespace CodeMemory.Tests;

sealed class MockStorageService : IStorageService
{
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

