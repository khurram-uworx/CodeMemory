using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

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

// Component resolution (build-file-first, directory fallback)
builder.Services.AddSingleton<CodeMemory.Services.Architecture.ProjectFileDetector>();
builder.Services.AddSingleton<CodeMemory.Services.Architecture.IComponentResolver, CodeMemory.Services.Architecture.ComponentResolver>();

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
            var repoName = context.Request.RouteValues["repoName"] as string;
            if (!string.IsNullOrEmpty(repoName))
            {
                var repoContext = context.RequestServices.GetRequiredService<IRepoContextAccessor>();
                repoContext.CurrentRepoName = repoName;
            }
            return Task.CompletedTask;
        };
    })
    .WithToolsFromAssembly(typeof(CodeMemory.Mcp.McpTools).Assembly)
    .WithToolsFromAssembly(typeof(CodeMemory.AspNet.Tools.AspNetSqlQueryTool).Assembly);

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

// RepoRegistry — EF Core registry DB for dynamic repo registration
var registryOptions = builder.Configuration
    .GetSection(RepoRegistryOptions.SectionName)
    .Get<RepoRegistryOptions>() ?? new();
builder.Services.AddSingleton(registryOptions);

var registryConnString = registryOptions.Provider.ToLowerInvariant() switch
{
    "sqlite" => builder.Configuration.GetConnectionString("Sqlite")
        ?? "Data Source=App_Data/registry.db",
    "sqlserver" => builder.Configuration.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException("Connection string 'SqlServer' is required for RepoRegistry SqlServer provider."),
    "npgsql" or "postgresql" => builder.Configuration.GetConnectionString("Npgsql")
        ?? builder.Configuration.GetConnectionString("PgVector")
        ?? throw new InvalidOperationException("Connection string 'Npgsql'/'PgVector' is required for RepoRegistry Npgsql provider."),
    var p => throw new InvalidOperationException(
        $"Unsupported RepoRegistry provider '{p}'. Supported: sqlite, sqlserver, npgsql")
};

builder.Services.AddDbContextFactory<RepoRegistryDbContext>(options =>
{
    switch (registryOptions.Provider.ToLowerInvariant())
    {
        case "sqlite":
            options.UseSqlite(registryConnString);
            break;
        case "sqlserver":
            options.UseSqlServer(registryConnString);
            break;
        case "npgsql":
        case "postgresql":
            options.UseNpgsql(registryConnString);
            break;
    }
});

builder.Services.AddRazorPages();
builder.Services.AddSingleton<RepoRegistryService>();
builder.Services.AddSingleton<CloneIndexService>();

var provider = builder.Configuration.GetValue<string>("Storage:Provider") ?? "inmemory";

var app = builder.Build();
app.UseCors();
app.MapRazorPages();

// Startup bootstrap: seed config → DB, load DB → ServiceRegistry
var bootstrapper = new StorageBootstrapper(app);
var allRepos = await bootstrapper.BootstrapAsync();

// Single catch-all MCP route
app.MapMcp("/api/mcp/{repoName}");

// Status endpoint
app.MapGet("/", () =>
{
    var service = "CodeMemory — Repository Intelligence Substrate";
    var repos = allRepos.Select(r =>
        new
        {
            name = r.Name,
            path = r.LocalPath,
            indexingCompleted = IndexingState.IsCompleted(r.Name)
        } as object);

    return Results.Ok(new
    {
        service,
        timestamp = DateTimeOffset.UtcNow,
        storageProvider = provider,
        repositories = repos
    });
});

// Registry status API — list all repos
app.MapGet("/api/repos", async (RepoRegistryService registry) =>
{
    var repos = await registry.ListAsync();
    var result = repos.Select(r => new
    {
        name = r.Name,
        source = r.GitUrl ?? r.LocalPath,
        cloneStatus = r.CloneStatus,
        indexStatus = r.IndexStatus,
        lastIndexedAt = r.LastIndexedAt,
        errorMessage = r.ErrorMessage,
        indexingCompleted = IndexingState.IsCompleted(r.Name)
    });

    return Results.Ok(new { repositories = result });
});

// Registry status API — single repo
app.MapGet("/api/repos/{name}/status", async (string name, RepoRegistryService registry) =>
{
    var repo = await registry.GetAsync(name);
    if (repo is null)
        return Results.NotFound(new { error = $"Repository '{name}' not found" });

    return Results.Ok(new
    {
        name = repo.Name,
        source = repo.GitUrl ?? repo.LocalPath,
        localPath = repo.LocalPath,
        cloneStatus = repo.CloneStatus,
        indexStatus = repo.IndexStatus,
        lastIndexedAt = repo.LastIndexedAt,
        errorMessage = repo.ErrorMessage,
        createdAt = repo.CreatedAt,
        indexingCompleted = IndexingState.IsCompleted(repo.Name)
    });
});

app.Run();
