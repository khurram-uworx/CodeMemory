using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using Microsoft.Extensions.Configuration;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Indexing.Search;
using CodeMemory.Services;
using CodeMemory.Services.Architecture;
using CodeMemory.Services.Git;
using CodeMemory.Services.Graph;
using CodeMemory.Services.Query;
using CodeMemory.Storage;
using Memori.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var repoRoot = args switch
{
    ["--repo", var path] => Path.GetFullPath(path),
    _ => Environment.CurrentDirectory
};

Console.Error.WriteLine($"CodeMemory MCP (stdio) — repo: {repoRoot}");

var builder = Host.CreateApplicationBuilder(args);

// Indexing services
builder.Services.AddSingleton<FileCrawler>();
builder.Services.AddSingleton<RoslynCSharpParser>();
builder.Services.AddSingleton<TreeSitterParser>();
builder.Services.AddSingleton<RoslynSymbolExtractor>();
builder.Services.AddSingleton<RoslynRelationshipExtractor>();
builder.Services.AddSingleton<TreeSitterSymbolExtractor>();
builder.Services.AddSingleton<TreeSitterRelationshipExtractor>();
builder.Services.AddSingleton<SemanticChunker>();

// Storage provider selection — "inmemory" (default) or "sqlite"
var provider = builder.Configuration.GetValue<string>("Storage:Provider") ?? "inmemory";

if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
{
    var memoryPath = Path.Combine(repoRoot, ".memorycode");
    Directory.CreateDirectory(memoryPath);
    var connectionString = $"Data Source={Path.Combine(memoryPath, "sqlvec.db")}";
    builder.Services.AddCodeMemorySqlliteStorage(repoRoot, connectionString);
}
else
{
    provider = "inmemory";
    builder.Services.AddCodeMemoryInMemoryStorage(repoRoot);
}

// Built-in n-gram embedding generator
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

builder.Services.AddSingleton<IndexingEngine>();

// Query services
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddSingleton<SymbolQueryService>();
builder.Services.AddSingleton<RelationshipQueryService>();

// Architecture intelligence services
builder.Services.AddSingleton<CodeMemory.Indexing.Graph.IDependencyGraphService, DependencyGraphService>();
builder.Services.AddSingleton<CodeMemory.Indexing.Architecture.IArchitectureService, ArchitectureService>();
builder.Services.AddSingleton<CodeMemory.Indexing.Architecture.IComponentClusteringService, ComponentClusteringService>();
builder.Services.AddSingleton<CodeMemory.Indexing.Git.IGitHistoryService, GitHistoryService>();
builder.Services.AddSingleton<CodeMemory.Mcp.Services.IEditContextService, CodeMemory.Mcp.Services.EditContextService>();

// MCP server (stdio transport)
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(CodeMemory.Mcp.McpTools).Assembly);

var host = builder.Build();

// Non-blocking: start indexing in background, serve MCP tools immediately
// Ping tool returns indexingCompleted=false until this finishes.
_ = Task.Run(async () =>
{
    try
    {
        var engine = host.Services.GetRequiredService<IndexingEngine>();
        await engine.RunIndexingAsync(repoRoot, CancellationToken.None);
        IndexingState.MarkCompleted(repoRoot);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Indexing failed: {ex.Message}");
    }
});

// Start MCP server loop immediately (reads JSON-RPC from stdin, writes to stdout)
await host.RunAsync();
