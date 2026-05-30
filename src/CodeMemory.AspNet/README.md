## Repository Portal (AspNet Razor Pages)

The AspNet host includes a web UI for managing repositories and browsing repository metrics:

### Features
- **Add repos** — specify a local path or GitHub URL (auto-cloned to `CloneBasePath`)
- **Status dashboard** — per-repo indexing status, clone progress, error messages, live SSE updates
- **Component browser** — view discovered build-file components (MsBuild, Maven, Node, Cargo, Go, etc.), classify their kind and type, soft-delete unwanted entries
- **Metrics** — local runtime metrics when enabled, plus repository code metrics for relational storage providers
- **Registry-backed** — `RepoRegistryDbContext` (SQLite/SQL Server/PostgreSQL) persists repo configurations across restarts

### Architecture
```
Razor Pages UI (GET/POST /Repos/Add, GET /)
  └─ RepoRegistryService
       ├─ ListAsync / GetAsync — read from RepoRegistryDbContext (EF Core)
       └─ CreateRepoAsync — insert new repo config
            └─ CloneIndexService
                 ├─ git clone (if URL) into CloneBasePath/{repoName}
                 ├─ create per-repo storage (IStorageService)
                 ├─ run IndexingEngine → store symbols/chunks/relationships
                 └─ register in ServiceRegistry for MCP endpoint
```

REST endpoints (`GET /api/repos`, `GET /api/repos/{name}/status`, `GET /api/repos/stream`) expose registry data for external monitoring and live SSE streaming.

### Metrics Page (`/Repos/{name}/Metrics`)

The metrics dashboard has two sections:

- **Runtime Metrics** — shown when `Observability:LocalMetrics:Enabled` is `true`. These are collected from the same `CodeMemory` meter used by OpenTelemetry and Prometheus, with bounded in-memory series storage for demos.
- **Repository Metrics** — shown when the repo uses a relational storage provider (`sqlite`, `pgvector`, or `sqlserver`). These query `IStorageService` through `MetricsService` to render:

- **Overview cards** — total symbols, files, classes, methods, interfaces, properties, fields, relationships
- **Symbol-kind distribution** — bar chart of counts per symbol kind
- **Method complexity** — average lines per method, longest methods table, size histogram (bucketed by line count)
- **Coupling analysis** — most-coupled symbols (highest outgoing relationship count), most-important symbols (highest incoming), relationship-type distribution
- **Top files by symbol count** — ranked table of files with the most symbols

Prometheus/Grafana and local runtime metrics can be enabled together; both consume the same emitted measurements.

### Component Management (`/Repos/{name}/Components`)

Displays all build-file-detected components for a repo. Each component row shows:
- Build file path, component name, configurable component kind (MsBuild/Maven/Node/Cargo/Go/Gradle/Python/CMake/Haskell/Folder) and type (Component/Test/Tool/Documentation/Example/Other)
- Editable via inline dropdowns; soft-delete via POST handler (hidden components do not reappear on re-index)

## Multi-Repo Architecture

### Design

Multi-repo support uses **`StorageServiceRouter` + `IRepoContextAccessor` (AsyncLocal) + per-repo MCP endpoints** — no keyed DI, no middleware, no `RequestServices` swap.

**How it works:** Each repo gets its own MCP endpoint via `MapMcp("/api/mcp/{repoName}")`. The MCP SDK's `ConfigureSessionOptions` callback extracts the repo name from the URL path and sets `IRepoContextAccessor.CurrentRepoName`. All services remain non-keyed — they depend on `IStorageService` which delegates to the correct per-repo storage via `StorageServiceRouter`.

### Data flow (multi-repo request)

```
HTTP POST /api/mcp/repo1
  └─ MCP handler (MapMcp("/api/mcp/repo1"))
       └─ ConfigureSessionOptions callback
            ├─ extracts "repo1" from URL path segments
            └─ sets IRepoContextAccessor.CurrentRepoName = "repo1"
                 └─ PerSessionExecutionContext preserves AsyncLocal for handler
                      └─ tool resolves IStorageService → StorageServiceRouter
                            └─ GetStorage() → registry.GetStorage("repo1") → repo1's storage
```

### Key components

| Component | Location | Purpose |
|---|---|---|
| `ServiceRegistry` / `IServiceRegistry` | `CodeMemory.AspNet/Configuration/` | Thread-safe `ConcurrentDictionary` of per-repo `IStorageService` instances; generalized from the old `IStorageServiceRegistry` |
| `StorageServiceRouter` | `CodeMemory.AspNet/Configuration/` | Delegates all 15 `IStorageService` methods via `GetStorage()` using ambient repo context |
| `IRepoContextAccessor` / `RepoContextAccessor` | `CodeMemory.AspNet/Configuration/` | `AsyncLocal<string?>` — singleton-safe, no scoped DI, flows with ExecutionContext |
| `ConfigureSessionOptions` | `CodeMemory.AspNet/Program.cs` | MCP SDK callback that extracts repo name from URL path |

### Constraints

- `IStorageService` is the **only** per-repo concern. All other services stay non-keyed (singleton).
- `Stateless = true` (Streamable HTTP) — no session affinity needed, each request is self-contained.
- `PerSessionExecutionContext = true` preserves `AsyncLocal` (and `IRepoContextAccessor`) across the handler chain.
- No middleware, no path rewriting, no `RequestServices` swap — clean ASP.NET pipeline.
- If no repos are configured, no MCP endpoints are registered at all.

### Indexing

`IndexingHostedService` sets `IRepoContextAccessor.CurrentRepoName` before each indexing iteration so `StorageServiceRouter` delegates to the correct DB. All storage services are initialized upfront before sequential indexing begins:

```csharp
// Initialize all storage services upfront
foreach (var (name, _) in repositories)
{
    var storage = registry.GetStorage(name);
    await storage.InitializeAsync(stoppingToken);
}

// Then index each repo sequentially
foreach (var (name, path) in repositories)
{
    repoContext.CurrentRepoName = name;
    repoContext.CurrentRepoRoot = repoPath;
    using var scope = serviceProvider.CreateScope();
    var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
    await engine.RunIndexingAsync(repoPath, stoppingToken);
}
```
