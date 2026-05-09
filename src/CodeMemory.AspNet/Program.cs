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

var builder = WebApplication.CreateBuilder(args);

// Indexing services
builder.Services.AddSingleton<FileCrawler>();
builder.Services.AddSingleton<ILanguageParser, RoslynCSharpParser>();
builder.Services.AddSingleton<RoslynSymbolExtractor>();
builder.Services.AddSingleton<RoslynRelationshipExtractor>();
builder.Services.AddSingleton<SemanticChunker>();

// Storage layer (.memorycode/{provider}.db relative to repo root)
// The provider name changes when swapping backends (e.g., "sqlvec", "pgvector").
// Users can gitignore .memorycode/ to exclude index databases from version control.
var repositories = builder.Configuration.GetSection("Repositories").Get<Dictionary<string, string>>();
var firstRepoName = repositories?.Keys.FirstOrDefault() ?? "main";
var repoPath = repositories?.TryGetValue(firstRepoName, out var path) == true 
    ? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path)) // AppContext.BaseDirectory
    : Environment.CurrentDirectory;
var provider = "sqlvec";
var memoryPath = Path.Combine(repoPath, ".memorycode");
Directory.CreateDirectory(memoryPath);
var connectionString = $"Data Source={Path.Combine(memoryPath, $"{provider}.db")}";
builder.Services.AddCodeMemoryStorage(connectionString);

// Built-in n-gram based embedding generator (zero config, no external deps)
// Replace by registering your own IEmbeddingGenerator before this line:
//   builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => ...);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

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

// MCP server
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
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
app.MapMcp("/api/mcp");

app.MapGet("/", () => "CodeMemory — Repository Intelligence Substrate");

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow,
    repo = repoPath,
    indexDb = Path.Combine(memoryPath, $"{provider}.db")
}));

app.Run();
