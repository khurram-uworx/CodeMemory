using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Git;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Indexing.Search;
using CodeMemory.Mcp;
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

IndexingState.SetVersion(version);

var (repoRootArg, debug, help, versionFlag, init) = CliParser.Parse(args);

if (help)
{
    Console.WriteLine($$"""
            Code Memory MCP Server v{{version}}

            Usage:
              --repo, -r <path>    Repository root path (default: current directory)
              --init, -i           Create .codememory.json with default settings and add to .gitignore
              --debug              Index synchronously with verbose logging (no MCP server)
              --version            Show version
              --help, -h           Show this help

            Examples:
              code-memory
              code-memory --init
              code-memory --repo C:\Projects\MyApp
              code-memory --repo ./my-project --init
              code-memory --repo ./my-project --debug

            Configure your agent with:
              npx -y @uworx/code-memory
            """);
    return 0;
}

if (versionFlag)
{
    Console.WriteLine($"Code Memory MCP Server v{version}");
    return 0;
}

var repoRoot = repoRootArg is not null ? Path.GetFullPath(repoRootArg) : Environment.CurrentDirectory;

if (init)
{
    var initService = new CodeMemoryInitService();
    var result = initService.Run(repoRoot);
    Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return result.Status == "ok" ? 0 : 1;
}

var mode = debug ? "debug" : "stdio";
Console.Error.WriteLine($"Code Memory MCP Server v{version} ({mode}) — repo: {repoRoot}");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddCodeMemoryMcp(options =>
{
    options.RepoRoot = repoRoot;
    options.DebugMode = debug;
    options.Version = version;
});

if (debug)
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
else
{
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new CodeMemory.Mcp.CodeMemoryFileLoggerProvider(repoRoot, version));
}

// Storage
builder.Services.AddCodeMemoryInMemoryStorage(repoRoot);

// Persistent git metric cache (file-backed, zero dependencies)
builder.Services.AddSingleton<IGitMetricStore, JsonGitMetricStore>();

// Indexing services
builder.Services.AddSingleton<FileCrawler>();
builder.Services.AddSingleton<RoslynCSharpParser>();
builder.Services.AddSingleton<TreeSitterParser>();
builder.Services.AddSingleton<RoslynSymbolExtractor>();
builder.Services.AddSingleton<RoslynRelationshipExtractor>();
builder.Services.AddSingleton<TreeSitterSymbolExtractor>();
builder.Services.AddSingleton<TreeSitterRelationshipExtractor>();
builder.Services.AddSingleton<SemanticChunker>();

// Init service (config template generation)
builder.Services.AddSingleton<CodeMemoryInitService>();

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

// File watcher (started after initial indexing completes)
builder.Services.AddSingleton(sp => new FileWatcherService(
    repoRoot,
    sp.GetRequiredService<IStorageService>(),
    sp.GetRequiredService<IndexingEngine>(),
    sp.GetRequiredService<ProjectFileDetector>(),
    sp.GetRequiredService<ILogger<FileWatcherService>>()));

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

// MCP server (stdio transport) — only in normal mode, not --debug
if (!debug)
{
    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(CodeMemory.Mcp.McpTools).Assembly)
        .WithToolsFromAssembly(typeof(CodeMemory.Mcp.Tools.SqlQueryTool).Assembly)
        .WithResourcesFromAssembly(typeof(CodeMemory.Mcp.McpTools).Assembly)
        .WithResourcesFromAssembly(typeof(SchemaResources).Assembly)
        .WithPromptsFromAssembly(typeof(CodeMemory.Mcp.McpTools).Assembly);
}

var app = builder.Build();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var embeddingGenerator = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

if (debug)
{
    // Debug mode: index synchronously with verbose console logging, then exit
    try
    {
        var engine = app.Services.GetRequiredService<IndexingEngine>();
        var progress = new Progress<double>(p => IndexingState.UpdateProgress(repoRoot, p));
        var result = await engine.RunIndexingAsync(repoRoot, CancellationToken.None, progress);
        IndexingState.MarkCompleted(repoRoot);
        IndexingState.StoreRelationshipCount(repoRoot, result.RelationshipCount);
        Console.Out.WriteLine();
        Console.Out.WriteLine("=== Indexing Complete ===");
        Console.Out.WriteLine($"  Repo: {repoRoot}");
        Console.Out.WriteLine($"  Status: success");
        Console.Out.WriteLine($"  Logs: stderr + .codememory/Log.*.txt");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Indexing failed: {ex.Message}");
        return 1;
    }
    return 0;
}

// Normal MCP mode: non-blocking indexing in background, serve tools immediately
// Ping tool returns indexingCompleted=false until this finishes, with progress %.
_ = Task.Run(async () =>
{
    try
    {
        var engine = app.Services.GetRequiredService<IndexingEngine>();
        var progress = new Progress<double>(p => IndexingState.UpdateProgress(repoRoot, p));
        var result = await engine.RunIndexingAsync(repoRoot, CancellationToken.None, progress);
        IndexingState.MarkCompleted(repoRoot);
        IndexingState.StoreRelationshipCount(repoRoot, result.RelationshipCount);

        var watcher = app.Services.GetRequiredService<FileWatcherService>();
        await watcher.StartAsync(CancellationToken.None);
        IndexingState.MarkFileWatcherActive();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Indexing failed: {ex.Message}");
    }
});

// Start MCP server loop immediately (reads JSON-RPC from stdin, writes to stdout)
await app.RunAsync();

return 0;
