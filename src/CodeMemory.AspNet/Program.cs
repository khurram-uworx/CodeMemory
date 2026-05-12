using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Services;
using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Indexing.Search;
using CodeMemory.Services;
using CodeMemory.Services.Architecture;
using CodeMemory.Services.Embedding;
using CodeMemory.Services.Git;
using CodeMemory.Services.Graph;
using CodeMemory.Services.Query;
using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

var builder = WebApplication.CreateBuilder(args);

// Indexing services
builder.Services.AddSingleton<FileCrawler>();
builder.Services.AddSingleton<ILanguageParser, RoslynCSharpParser>();
builder.Services.AddSingleton<RoslynSymbolExtractor>();
builder.Services.AddSingleton<RoslynRelationshipExtractor>();
builder.Services.AddSingleton<SemanticChunker>();

// Repo-agnostic: embedding generator (registered before repo loop)
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

// Build per-repo storage registry
// Embedding generator is optional in StorageService (defaults to 1536 dimensions).
// IndexingEngine resolves IEmbeddingGenerator from DI separately.
var repositories = builder.Configuration.GetSection("Repositories").Get<Dictionary<string, string>>();
var storageRegistry = new StorageServiceRegistry();
var repoInfos = new List<(string name, string path, string dbPath)>();

var provider = "sqlvec";
foreach (var (name, path) in repositories ?? [])
{
    var repoRoot = Path.IsPathRooted(path) ? path
        : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
    var memoryPath = Path.Combine(repoRoot, ".memorycode");
    Directory.CreateDirectory(memoryPath);
    var connectionString = $"Data Source={Path.Combine(memoryPath, $"{provider}.db")}";

    var store = new SqliteVectorStore(connectionString);
    var storageService = new StorageService(store, embeddingGenerator: null);
    storageRegistry.Register(name, storageService);
    repoInfos.Add((name, repoRoot, Path.Combine(memoryPath, $"{provider}.db")));
}

builder.Services.AddSingleton<IStorageServiceRegistry>(storageRegistry);
builder.Services.AddSingleton<IRepoContextAccessor, RepoContextAccessor>();
builder.Services.AddSingleton<IStorageService, StorageServiceRouter>();

builder.Services.AddScoped<IndexingEngine>();
builder.Services.AddHostedService<IndexingHostedService>();

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

// Per-repo MCP endpoints — always requires repo name in URL for storage routing
//   e.g. POST /api/mcp/codememory, POST /api/mcp/default, etc.
// No bare /api/mcp endpoint. ConfigureSessionOptions extracts the repo name from the URL.
foreach (var (name, _, _) in repoInfos)
{
    app.MapMcp($"/api/mcp/{name}");
}

app.MapGet("/", () => Results.Ok(new
{
    service = "CodeMemory — Repository Intelligence Substrate",
    repositories = repoInfos.Select(r => new { name = r.name, path = r.path, indexDb = r.dbPath })
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow,
    repositories = repoInfos.Select(r => new { name = r.name, path = r.path, indexDb = r.dbPath })
}));

app.Run();
