using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Indexing.Search;
using CodeMemory.Services;
using CodeMemory.Services.Embedding;
using CodeMemory.Services.Query;
using CodeMemory.Storage;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

// Indexing services
builder.Services.AddSingleton<FileCrawler>();
builder.Services.AddSingleton<ILanguageParser, RoslynCSharpParser>();
builder.Services.AddSingleton<RoslynSymbolExtractor>();
builder.Services.AddSingleton<SemanticChunker>();

// Storage layer (.index/codememory.db relative to repo root)
var repoRoot = Environment.CurrentDirectory;
var indexPath = Path.Combine(repoRoot, ".index");
Directory.CreateDirectory(indexPath);
var connectionString = $"Data Source={Path.Combine(indexPath, "codememory.db")}";
builder.Services.AddCodeMemoryStorage(connectionString);

// Built-in n-gram based embedding generator (zero config, no external deps)
// Replace by registering your own IEmbeddingGenerator before this line:
//   builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => ...);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

builder.Services.AddHostedService<IndexingService>();

// Query services
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddSingleton<SymbolQueryService>();
builder.Services.AddSingleton<RelationshipQueryService>();

// MCP server
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

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
    repo = repoRoot,
    indexDb = Path.Combine(indexPath, "codememory.db")
}));

app.Run();
