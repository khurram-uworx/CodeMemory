using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Services;
using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Indexing.Search;
using CodeMemory.Mcp.Services;
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

var repositories = builder.Configuration.GetSection("Repositories").Get<Dictionary<string, string>>();

// Built-in n-gram based embedding generator (zero config, no external deps)
// Replace by registering your own IEmbeddingGenerator before this line:
//   builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => ...);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

// Multi-repo: register per-repo keyed services
if (repositories is { Count: > 0 })
{
    foreach (var kvp in repositories)
    {
        var repoName = kvp.Key;
        var repoRoot = Path.IsPathRooted(kvp.Value) ? kvp.Value : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, kvp.Value));
        var repoMemoryDir = Path.Combine(repoRoot, ".memorycode");
        Directory.CreateDirectory(repoMemoryDir);
        var repoConnStr = $"Data Source={Path.Combine(repoMemoryDir, "sqlvec.db")}";

        builder.Services.AddKeyedSingleton<IStorageService>(repoName, (sp, key) =>
            ServiceCollectionExtensions.CreateStorageService(
                repoConnStr,
                sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));

        builder.Services.AddKeyedSingleton<ISemanticSearchService>(repoName, (sp, key) =>
            new SemanticSearchService(
                sp.GetRequiredKeyedService<IStorageService>(key),
                sp.GetRequiredService<ILogger<SemanticSearchService>>(),
                sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));

        builder.Services.AddKeyedSingleton<SymbolQueryService>(repoName, (sp, key) =>
            new SymbolQueryService(sp.GetRequiredKeyedService<IStorageService>(key)));

        builder.Services.AddKeyedSingleton<RelationshipQueryService>(repoName, (sp, key) =>
            new RelationshipQueryService(sp.GetRequiredKeyedService<IStorageService>(key)));

        builder.Services.AddKeyedSingleton<CodeMemory.Indexing.Graph.IDependencyGraphService>(repoName, (sp, key) =>
            new DependencyGraphService(
                sp.GetRequiredKeyedService<IStorageService>(key),
                sp.GetRequiredService<ILogger<DependencyGraphService>>()));

        builder.Services.AddKeyedSingleton<CodeMemory.Indexing.Architecture.IArchitectureService>(repoName, (sp, key) =>
            new ArchitectureService(
                sp.GetRequiredKeyedService<IStorageService>(key),
                sp.GetRequiredService<ILogger<ArchitectureService>>()));

        builder.Services.AddKeyedSingleton<CodeMemory.Indexing.Architecture.IComponentClusteringService>(repoName, (sp, key) =>
            new ComponentClusteringService(
                sp.GetRequiredKeyedService<IStorageService>(key),
                sp.GetRequiredService<ILogger<ComponentClusteringService>>()));

        builder.Services.AddKeyedSingleton<CodeMemory.Indexing.Git.IGitHistoryService>(repoName, (sp, key) =>
            new GitHistoryService(
                sp.GetRequiredKeyedService<IStorageService>(key),
                sp.GetRequiredService<ILogger<GitHistoryService>>(),
                repoRoot));

        builder.Services.AddKeyedSingleton<IEditContextService>(repoName, (sp, key) =>
            new EditContextService(sp));
    }
}

// Non-keyed fallback: first repo's storage (for IndexingEngine and legacy consumers)
if (repositories is { Count: > 0 })
{
    var firstKey = repositories.Keys.First();
    builder.Services.AddSingleton<IStorageService>(sp =>
        sp.GetRequiredKeyedService<IStorageService>(firstKey));
}

builder.Services.AddScoped<IndexingEngine>();
builder.Services.AddHostedService<IndexingHostedService>();

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
app.UseRepoScopedMcp();
app.MapMcp("/api/mcp");
app.MapMcp("/api/mcp/{repoName}");

app.MapGet("/", () => "CodeMemory — Repository Intelligence Substrate");

app.MapGet("/health", () =>
{
    var repos = new List<object>();
    if (repositories is not null)
    {
        foreach (var (name, path) in repositories)
        {
            var repoRoot = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
            repos.Add(new
            {
                name,
                path = repoRoot,
                indexDb = Path.Combine(repoRoot, ".memorycode", "sqlvec.db")
            });
        }
    }
    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTimeOffset.UtcNow,
        repositories = repos
    });
});

app.Run();
