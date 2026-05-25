using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Scheduling;
using CodeMemory.AspNet.Services;
using CodeMemory.AspNet.Storage;
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
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

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
// Configurable via Embedding:Provider in appsettings.json:
//   "ngram" (default, no model needed)
//   "onnx"  (raw ONNX Runtime, requires bge-micro-v2 model)
//   "sk-connector-onnx" (SK ONNX connector, requires bge-micro-v2 model)
//   "ollama" (Ollama server, requires Ollama running at Embedding:OllamaEndpoint)
var embeddingProvider = builder.Configuration.GetValue<string>("Embedding:Provider") ?? "ngram";
switch (embeddingProvider)
{
    //case "onnx":
    //    {
    //        var modelPath = Path.GetFullPath(
    //            builder.Configuration.GetValue<string>("Embedding:OnnxModelPath")
    //            ?? "models/bge-micro-v2/model.onnx");
    //        var vocabPath = Path.GetFullPath(
    //            builder.Configuration.GetValue<string>("Embedding:OnnxVocabPath")
    //            ?? "models/bge-micro-v2/vocab.txt");
    //        builder.Services.AddCodeMemoryOnnxEmbeddingGenerator(modelPath, vocabPath);
    //        break;
    //    }
    //case "sk-connector-onnx":
    //    {
    //        var modelPath = Path.GetFullPath(
    //            builder.Configuration.GetValue<string>("Embedding:OnnxModelPath")
    //            ?? "models/bge-micro-v2/model.onnx");
    //        var vocabPath = Path.GetFullPath(
    //            builder.Configuration.GetValue<string>("Embedding:OnnxVocabPath")
    //            ?? "models/bge-micro-v2/vocab.txt");
    //        builder.Services.AddCodeMemorySKOnnxEmbeddingGenerator(modelPath, vocabPath);
    //        break;
    //    }
    //case "ollama":
    //    {
    //        var endpoint = builder.Configuration.GetValue<string>("Embedding:OllamaEndpoint")
    //            ?? "http://localhost:11434";
    //        var model = builder.Configuration.GetValue<string>("Embedding:OllamaModel")
    //            ?? "all-minilm";
    //        builder.Services.AddCodeMemoryOllamaEmbeddingGenerator(
    //            new Uri(endpoint), model);
    //        break;
    //    }
    default:
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();
        break;
}

var storageRegistry = new ServiceRegistry();
builder.Services.AddSingleton<IServiceRegistry>(storageRegistry);
builder.Services.AddSingleton<IRepoContextAccessor, RepoContextAccessor>();
builder.Services.AddSingleton<IStorageService, StorageServiceRouter>();

builder.Services.AddScoped<IndexingEngine>();
builder.Services.AddHostedService<IndexingHostedService>();
builder.Services.Configure<RebuildOptions>(builder.Configuration.GetSection("RebuildIndex"));
builder.Services.Configure<IndexingOptions>(builder.Configuration.GetSection(IndexingOptions.SectionName));
builder.Services.AddHostedService<RebuildIndexHostedService>();

// Query services
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddSingleton<SymbolQueryService>();
builder.Services.AddSingleton<RelationshipQueryService>();

// Component resolution (build-file-first, directory fallback)
builder.Services.AddSingleton<ProjectFileDetector>();
builder.Services.AddSingleton<IComponentResolver, ComponentResolver>();

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
    .WithToolsFromAssembly(typeof(CodeMemory.AspNet.Tools.AspNetMcpTools).Assembly)
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

// RepoRegistry — EF Core registry DB for dynamic repo registration
// Provider is derived from Storage:Provider to simplify configuration
var registryOptions = builder.Configuration
    .GetSection(RepoRegistryOptions.SectionName)
    .Get<RepoRegistryOptions>() ?? new();
builder.Services.AddSingleton(registryOptions);

var provider = builder.Configuration.GetValue<string>("Storage:Provider") ?? "inmemory";

builder.Services.AddSingleton<StorageFactory>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var embeddingGenerator = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    var configuration = sp.GetRequiredService<IConfiguration>();
    var registryDbFactory = sp.GetRequiredService<IDbContextFactory<RepoRegistryDbContext>>();

    return (repoName, repoPath, repoId) =>
    {
        if (string.Equals(provider, "inmemory", StringComparison.OrdinalIgnoreCase))
        {
            return new StorageService(repoPath,
                loggerFactory.CreateLogger<StorageService>(),
                new Memori.Storage.InMemoryVectorStore(), embeddingGenerator);
        }

        if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var memoryPath = Path.Combine(repoPath, ".codememory");
            Directory.CreateDirectory(memoryPath);
            var connString = $"Data Source={Path.Combine(memoryPath, "sqlvec.db")}";
            return CodeMemory.AspNet.Storage.ServiceCollectionExtensions.createSqliteStorage(
                repoPath, repoId, connString, registryDbFactory,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
        }

        if (string.Equals(provider, "pgvector", StringComparison.OrdinalIgnoreCase))
        {
            var connString = configuration.GetConnectionString("PgVector")
                ?? throw new InvalidOperationException(
                    "Connection string 'PgVector' is required when Storage:Provider is 'pgvector'");
            var schema = CodeMemory.AspNet.Storage.ServiceCollectionExtensions.sanitizeSchemaName(repoName);
            return CodeMemory.AspNet.Storage.ServiceCollectionExtensions.CreatePgVectorStorage(
                repoPath, repoId, connString, schema, registryDbFactory,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
        }

        if (string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            var connString = configuration.GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException(
                    "Connection string 'SqlServer' is required when Storage:Provider is 'sqlserver'");
            var schema = CodeMemory.AspNet.Storage.ServiceCollectionExtensions.sanitizeSchemaName(repoName);
            return CodeMemory.AspNet.Storage.ServiceCollectionExtensions.createSqlServerStorage(
                repoPath, repoId, connString, schema, registryDbFactory,
                loggerFactory.CreateLogger<HybridStorageService>(), embeddingGenerator);
        }

        throw new InvalidOperationException(
            $"Unsupported storage provider '{provider}'. Supported: inmemory, sqlite, pgvector, sqlserver");
    };
});

(string? registryConnString, string registryProvider) = provider.ToLowerInvariant() switch
{
    "inmemory" => (null, "inmemory"),
    "sqlite" => (builder.Configuration.GetConnectionString("Sqlite")
        ?? "Data Source=App_Data/registry.db", "sqlite"),
    "pgvector" => (builder.Configuration.GetConnectionString("Npgsql")
        ?? builder.Configuration.GetConnectionString("PgVector")
        ?? throw new InvalidOperationException("Connection string 'Npgsql'/'PgVector' is required when Storage:Provider is 'pgvector'"), "npgsql"),
    "sqlserver" => (builder.Configuration.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException("Connection string 'SqlServer' is required when Storage:Provider is 'sqlserver'"), "sqlserver"),
    var p => throw new InvalidOperationException(
        $"Unsupported storage provider '{p}'. Supported: inmemory, sqlite, pgvector, sqlserver")
};

builder.Services.AddDbContextFactory<RepoRegistryDbContext>(options =>
{
    switch (registryProvider)
    {
        case "inmemory":
            options.UseInMemoryDatabase("codememory-registry");
            break;
        case "sqlite":
            options.UseSqlite(registryConnString);
            break;
        case "sqlserver":
            options.UseSqlServer(registryConnString);
            break;
        case "npgsql":
            options.UseNpgsql(registryConnString);
            break;
    }
});

builder.Services.AddRazorPages();
builder.Services.AddSingleton<RepoRegistryService>();
builder.Services.AddSingleton<CloneIndexService>();
builder.Services.AddSingleton<NotificationService>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseCors();
app.MapRazorPages();

// Startup bootstrap: seed config → DB, load DB → ServiceRegistry
var bootstrapper = new StorageBootstrapper(app, provider);
var allRepos = await bootstrapper.BootstrapAsync();

// Single catch-all MCP route
app.MapMcp("/api/mcp/{repoName}");

// Status/Health endpoint
app.MapGet("/health", async (RepoRegistryService registryService) =>
{
    var service = "CodeMemory — Repository Intelligence Substrate";
    var repos = allRepos.Select(r =>
    {
        var progress = IndexingState.GetProgress(r.Name);
        return new
        {
            name = r.Name,
            path = r.LocalPath,
            indexingCompleted = IndexingState.IsCompleted(r.Name),
            indexingProgress = progress
        } as object;
    });

    var allRegistered = await registryService.ListAsync();
    var failedRepos = allRegistered
        .Where(r => r.IndexStatus == "Failed" || r.CloneStatus == "Failed")
        .Select(r => new { name = r.Name, status = r.CloneStatus == "Failed" ? r.CloneStatus : r.IndexStatus, error = r.ErrorMessage })
        .ToList();

    return Results.Ok(new
    {
        service,
        timestamp = DateTimeOffset.UtcNow,
        storageProvider = provider,
        repositories = repos,
        failedRepoCount = failedRepos.Count,
        failedRepos
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

// SSE stream — live repo status for the dashboard
app.MapGet("/api/repos/stream", async (HttpContext context, RepoRegistryService registry, NotificationService notifications) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    while (!context.RequestAborted.IsCancellationRequested)
    {
        var repos = await registry.ListAsync();
        var data = repos.Select(r => new
        {
            name = r.Name,
            source = r.GitUrl ?? r.LocalPath,
            cloneStatus = r.CloneStatus,
            indexStatus = r.IndexStatus,
            lastIndexedAt = r.LastIndexedAt,
            errorMessage = r.ErrorMessage,
            indexingCompleted = IndexingState.IsCompleted(r.Name),
            indexingProgress = IndexingState.GetProgress(r.Name)
        });

        var message = notifications.TryDequeue();

        var json = JsonSerializer.Serialize(new { repositories = data, message });
        await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await Task.Delay(2000, context.RequestAborted);
    }
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
