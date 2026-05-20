using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
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
using Microsoft.Extensions.Logging;
using System.Reflection;

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "unknown";

if (args is ["--help"] or ["-h"] or ["--version"] or ["-v"])
{
    Console.WriteLine($"CodeMemory MCP v{version}");
    Console.WriteLine();
    Console.WriteLine("Configure your Coding Agent / IDE with command \"npx -y @uworx/code-memory\"");
    return;
}

var repoRoot = args switch
{
    ["--repo", var path] => Path.GetFullPath(path),
    _ => Environment.CurrentDirectory
};

Console.Error.WriteLine($"CodeMemory MCP v{version} (stdio) — repo: {repoRoot}");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddProvider(new CodeMemory.Mcp.CodeMemoryFileLoggerProvider());

if (!builder.Environment.IsDevelopment())
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Storage
builder.Services.AddCodeMemoryInMemoryStorage(repoRoot);

// Indexing services
builder.Services.AddSingleton<FileCrawler>();
builder.Services.AddSingleton<RoslynCSharpParser>();
builder.Services.AddSingleton<TreeSitterParser>();
builder.Services.AddSingleton<RoslynSymbolExtractor>();
builder.Services.AddSingleton<RoslynRelationshipExtractor>();
builder.Services.AddSingleton<TreeSitterSymbolExtractor>();
builder.Services.AddSingleton<TreeSitterRelationshipExtractor>();
builder.Services.AddSingleton<SemanticChunker>();

// SQL query services (InMemoryVectorStore backend)
builder.Services.AddSingleton<CodeMemory.Mcp.SqlQuery.CollectionRegistry>();
builder.Services.AddSingleton<CodeMemory.Mcp.SqlQuery.SqlQueryService>();
builder.Services.AddSingleton<CodeMemory.Mcp.SqlQuery.TableSchemaProvider>();

// Built-in n-gram embedding generator
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

// Component resolution (build-file-first, directory fallback)
builder.Services.AddSingleton<ProjectFileDetector>();
builder.Services.AddSingleton<IComponentResolver, ComponentResolver>();

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
    .WithToolsFromAssembly(typeof(CodeMemory.Mcp.McpTools).Assembly)
    .WithToolsFromAssembly(typeof(CodeMemory.Mcp.Tools.SqlQueryTool).Assembly);

var app = builder.Build();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var embeddingGenerator = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();


// Non-blocking: start indexing in background, serve MCP tools immediately
// Ping tool returns indexingCompleted=false until this finishes.
_ = Task.Run(async () =>
{
    try
    {
        var engine = app.Services.GetRequiredService<IndexingEngine>();
        await engine.RunIndexingAsync(repoRoot, CancellationToken.None);
        IndexingState.MarkCompleted(repoRoot);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Indexing failed: {ex.Message}");
    }
});

// Start MCP server loop immediately (reads JSON-RPC from stdin, writes to stdout)
await app.RunAsync();
