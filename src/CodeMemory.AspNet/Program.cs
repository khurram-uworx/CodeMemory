using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Services;
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
using Memori.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

var builder = WebApplication.CreateBuilder(args);

// Indexing services
builder.Services.AddSingleton<FileCrawler>();
builder.Services.AddSingleton<RoslynCSharpParser>();
builder.Services.AddSingleton<TreeSitterParser>();
builder.Services.AddSingleton<RoslynSymbolExtractor>();
builder.Services.AddSingleton<RoslynRelationshipExtractor>();
builder.Services.AddSingleton<TreeSitterSymbolExtractor>();
builder.Services.AddSingleton<TreeSitterRelationshipExtractor>();
builder.Services.AddSingleton<SemanticChunker>();

// Repo-agnostic: embedding generator (registered before repo loop)
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

var storageRegistry = new ServiceRegistry();
builder.Services.AddSingleton<IServiceRegistry>(storageRegistry);
builder.Services.AddSingleton<IRepoContextAccessor, RepoContextAccessor>();
builder.Services.AddSingleton<IStorageService, StorageServiceRouter>();

builder.Services.AddScoped<IndexingEngine>();
builder.Services.AddHostedService<IndexingHostedService>();

// Query services
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddSingleton<SymbolQueryService>();
builder.Services.AddSingleton<RelationshipQueryService>();

var provider = builder.Configuration.GetValue<string>("Storage:Provider") ?? "inmemory";
var useSqlite = string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase);

if (!useSqlite)
{
    provider = "inmemory";

    // SQL query services (InMemoryVectorStore backend)
    //builder.Services.AddSingleton<CodeMemory.SqlQuery.CollectionRegistry>();
    //builder.Services.AddSingleton<CodeMemory.SqlQuery.SqlQueryService>();
    //builder.Services.AddSingleton<CodeMemory.SqlQuery.TableSchemaProvider>();
    // we are moving SQL query services to CodeMemory.Mcp; inmemory will only be supported there
    // inmemroy support will be removed from ASP.NET soon
}

// Architecture intelligence services
builder.Services.AddSingleton<CodeMemory.Indexing.Graph.IDependencyGraphService, DependencyGraphService>();
builder.Services.AddSingleton<CodeMemory.Indexing.Architecture.IArchitectureService, ArchitectureService>();
builder.Services.AddSingleton<CodeMemory.Indexing.Architecture.IComponentClusteringService, ComponentClusteringService>();
builder.Services.AddSingleton<CodeMemory.Indexing.Git.IGitHistoryService, GitHistoryService>();
builder.Services.AddSingleton<CodeMemory.Mcp.Services.IEditContextService, CodeMemory.Mcp.Services.EditContextService>();

// MCP server with per-request repo context via ConfigureSessionOptions
// PerSessionExecutionContext ensures AsyncLocal values (IRepoContextAccessor) flow to tool handlers.
// Streamable HTTP transport is used (no legacy SSE — use /api/mcp/{repo} POST endpoint).
builder.Services.AddMcpServer()
    .WithHttpTransport(o =>
    {
        o.Stateless = true;
        o.PerSessionExecutionContext = true;
        o.ConfigureSessionOptions = (context, mcpOptions, ct) =>
        {
            var path = context.Request.Path.Value;
            if (path is not null)
            {
                // Extract repo name from /api/mcp/{repoName}
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 3
                    && string.Equals(segments[^2], "mcp", StringComparison.OrdinalIgnoreCase))
                {
                    var repoContext = context.RequestServices.GetRequiredService<IRepoContextAccessor>();
                    repoContext.CurrentRepoName = segments[^1];
                }
            }
            return Task.CompletedTask;
        };
    })
    .WithToolsFromAssembly(typeof(CodeMemory.Mcp.McpTools).Assembly);
//.WithToolsFromAssembly(typeof(CodeMemory.AspNet.Tools.AnyAspNetTool).Assembly);

// CORS — origins configured in appsettings.json:Cors:AllowedOrigins
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsOrigins is { Length: > 0 })
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
        else
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();

// Build per-repo storage registry
// Embedding generator is optional in StorageService (defaults to 1536 dimensions).
// IndexingEngine resolves IEmbeddingGenerator from DI separately.
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var embeddingGenerator = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var repositories = builder.Configuration.GetSection("Repositories").Get<Dictionary<string, string>>();
var repoInfos = new List<(string name, string path, string? dbPath)>();

foreach (var (name, path) in repositories ?? [])
{
    var repoRoot = Path.IsPathRooted(path) ? path
        : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
    var memoryPath = Path.Combine(repoRoot, ".memorycode");
    Directory.CreateDirectory(memoryPath);

    if (useSqlite)
    {
        var connectionString = $"Data Source={Path.Combine(memoryPath, "sqlvec.db")}";
        var store = new Microsoft.SemanticKernel.Connectors.SqliteVec.SqliteVectorStore(connectionString);
        var storageService = new StorageService(repoRoot, loggerFactory.CreateLogger<StorageService>(), store, embeddingGenerator);
        storageRegistry.Register(name, storageService);
        repoInfos.Add((name, repoRoot, Path.Combine(memoryPath, "sqlvec.db")));
    }
    else
    {
        var store = new InMemoryVectorStore();
        var storageService = new StorageService(repoRoot, loggerFactory.CreateLogger<StorageService>(), store, embeddingGenerator);
        storageRegistry.Register(name, storageService);
        repoInfos.Add((name, repoRoot, null));
    }
}

// Per-repo MCP endpoints — always requires repo name in URL for storage routing
//   e.g. POST /api/mcp/codememory, POST /api/mcp/default, etc.
// No bare /api/mcp endpoint. ConfigureSessionOptions extracts the repo name from the URL.
foreach (var (name, _, _) in repoInfos)
    app.MapMcp($"/api/mcp/{name}");

app.MapGet("/", () =>
{
    var service = "CodeMemory — Repository Intelligence Substrate";
    if (useSqlite)
    {
        var repos = repoInfos.Select(r => new
        {
            r.name,
            r.path,
            indexDb = r.dbPath,
            indexingCompleted = IndexingState.IsCompleted(r.name)
        });
        return Results.Ok(new
        {
            service,
            timestamp = DateTimeOffset.UtcNow,
            storageProvider = provider,
            repositories = repos
        });
    }
    else
    {
        var repos = repoInfos.Select(r => new
        {
            r.name,
            r.path,
            indexingCompleted = IndexingState.IsCompleted(r.name)
        });
        return Results.Ok(new
        {
            service,
            timestamp = DateTimeOffset.UtcNow,
            storageProvider = provider,
            repositories = repos
        });
    }
});

app.Run();
