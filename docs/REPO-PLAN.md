# Multi-Repo MCP Support

## Purpose

This plan breaks the multi-repo feature (detect repo name from `/api/mcp/{repoName}`, route to per-repo keyed services) into concrete, assignable tasks for coding agents.

See `ARCHITECTURE.md` for the overall system architecture. The existing multi-repo scaffolding (`RepoScopedMiddleware`, `RepoScopedServices`, `MultiRepoServiceFactory`, `RepoContextAccessor`) was built in a prior pass but was never wired into `Program.cs` — the app still uses a single-repo path.

---

## Suggested Execution Order

1. **Task 1** — Program.cs: keyed DI registration for per-repo services (decision gate: service shape)
2. **Task 2** — RepoScopedServices: add root-provider fallthrough (prerequisite for middleware)
3. **Task 3** — URL-based repo detection middleware + path rewrite
4. **Task 4** — Wire everything in Program.cs and remove single-repo path
5. **Task 5** — Integration tests

## Coordination Notes

- Tasks 1-3 can be done in parallel by independent agents (they touch different files).
- Task 4 must wait for Tasks 1-3 to complete.
- Task 5 can start as soon as Task 4 is done, or earlier if tests are written against known interfaces.
- `Program.cs` is the merge-conflict hotspot — all wiring tasks converge there.

---

## Task 1: Keyed DI Registration in Program.cs

### Priority

High

### Goal

Register `IStorageService` and all dependent services as **keyed singletons** per repo name, so `RepoScopedServices` can resolve them via `GetKeyedService<T>(repoName)`.

### Why this exists

Currently `Program.cs:37` calls `builder.Services.AddCodeMemoryStorage(connectionString)` which registers a single, non-keyed `IStorageService`. For multi-repo, we need one `IStorageService` instance per repo (each pointing at a different SQLite DB in `.memorycode/`). All services that depend on it (`DependencyGraphService`, `ArchitectureService`, etc.) must also be keyed per repo.

### Decision required

Service collection shape — should each repo get its own instance of `DependencyGraphService`, `ArchitectureService`, etc., or should they be shared singletons that rely on a keyed `IStorageService`? **Recommended: keyed per repo** — it's cleaner, avoids state leakage (e.g., `GitHistoryService` has a per-repo `repoRoot` field and cache), and maps 1:1 to the `RepoScopedServices` pattern.

### Scope

- Refactor the storage registration loop to create per-repo `IStorageService` using `AddKeyedSingleton`
- Register `ISemanticSearchService`, `SymbolQueryService`, `RelationshipQueryService`, `IDependencyGraphService`, `IArchitectureService`, `IComponentClusteringService`, `IGitHistoryService`, `IEditContextService` as keyed singletons with the same repo key
- `NgramEmbeddingGenerator` is repo-agnostic — keep as non-keyed singleton
- Keep `MultiRepoServiceFactory` as a fallback registry (or remove if no longer needed — verify callers)

### Constraints

- Connection string per repo: `Data Source={repoRoot}/.memorycode/sqlvec.db`
- Directory must be created before `AddSqliteVectorStore` is called
- The `Repositories` config section is already parsed by `IndexingHostedService` — reuse the same pattern but at DI registration time
- **SDK behavior confirmed**: `MapMcp` uses `endpoints.MapGroup(pattern)` (endpoint routing, not middleware branching). In stateless mode, the `StreamableHttpHandler` resolves per-request services from `context.RequestServices`, not the root provider. This confirms that swapping `RequestServices` via middleware will correctly inject per-repo services into the MCP handler.

### Suggested implementation path

```csharp
foreach (var (name, path) in repositories)
{
    var repoRoot = ...; // resolve absolute path
    var connStr = $"Data Source={repoRoot}/.memorycode/sqlvec.db";
    // Create a nested ServiceCollection? No — use AddKeyedSingleton with factory
    services.AddKeyedSingleton<IStorageService>(name, (sp, key) =>
    {
        var store = ...; // create VectorStore
        var gen = sp.GetService<IEmbeddingGenerator<...>>();
        return new StorageService(store, gen, 1536);
    });
    services.AddKeyedSingleton<ISemanticSearchService>(name, (sp, key) =>
        new SemanticSearchService(
            sp.GetRequiredKeyedService<IStorageService>(key),
            sp.GetRequiredService<ILogger<SemanticSearchService>>(),
            sp.GetService<IEmbeddingGenerator<...>>()));
    // ... repeat for all 7 dependent services
}
```

### Acceptance criteria

- A service provider built with two configured repos has two distinct `IStorageService` instances, one per repo key
- `sp.GetKeyedService<IStorageService>("repo1")` returns the correct instance
- `sp.GetKeyedService<IDependencyGraphService>("repo1")` returns an instance wired to repo1's storage
- Non-keyed `GetService<IStorageService>()` returns `null` (no default fallback)

### Files likely involved

- `src/CodeMemory.AspNet/Program.cs`
- `src/CodeMemory.Storage/ServiceCollectionExtensions.cs` (may need an overload that returns an `IStorageService` directly without auto-registering)

---

## Task 2: RepoScopedServices Root-Provider Fallthrough

### Priority

High

### Goal

Add a fallback to the root `IServiceProvider` in `RepoScopedServices.GetService()` so that infrastructure types (loggers, `IOptions`, `IConfiguration`, etc.) resolve correctly.

### Why this exists

`RepoScopedServices.GetService()` returns `null` for any type not in its hardcoded switch list (`nameof(IStorageService)`, `nameof(ISemanticSearchService)`, etc.). When the middleware swaps `context.RequestServices` to `RepoScopedServices`, the MCP framework tries to resolve loggers, `IOptions<McpServerOptions>`, and other DI infrastructure through this provider.

**This is not a nice-to-have — it's a hard requirement.** The MCP SDK's `StreamableHttpHandler` receives `ILoggerFactory` and `IOptions<McpServerOptions>` via constructor injection (root provider) but creates the per-request `McpServer` using `context.RequestServices`. Without the fallthrough, `McpServer.Create()` will fail to resolve its dependencies and throw, breaking every MCP request.

### Scope

- Change the `_ => null` fallback to `_ => root.GetService(serviceType)`
- Ensure this doesn't inadvertently bypass scoped lifecycle for infrastructure types (loggers are typically singletons, so it's safe)
- Verify `IServiceProvider` is passed correctly in the constructor

### Suggested implementation path

```csharp
public object? GetService(Type serviceType)
{
    return serviceType.Name switch
    {
        nameof(IStorageService) => root.GetKeyedService<IStorageService>(repoName),
        nameof(ISemanticSearchService) => root.GetKeyedService<ISemanticSearchService>(repoName),
        // ... all other known types ...
        _ => root.GetService(serviceType),  // fallback instead of null
    };
}
```

### Acceptance criteria

- When `RepoScopedServices` is queried for `ILogger<SomeTool>`, it resolves correctly from the root provider
- When queried for `ISemanticSearchService`, it still returns the keyed per-repo instance
- No `NullReferenceException` or missing-registration errors when MCP tools resolve their dependencies

### Files likely involved

- `src/CodeMemory.AspNet/Configuration/RepoScopedServices.cs`

---

## Task 3: URL-Based Repo Detection Middleware

### Priority

High

### Goal

Create a middleware (or update `RepoScopedMiddleware`) that extracts the repo name from the URL path `/api/mcp/{repoName}` and rewrites the path to `/api/mcp` before the MCP handler runs.

### Why this exists

`app.MapMcp("/api/mcp")` binds to a fixed path using `endpoints.MapGroup(pattern)` (endpoint routing). To support `/api/mcp/repo1` and `/api/mcp/repo2`, we need either multiple `MapMcp` calls (clunky, requires knowing all repos at startup) or a middleware that detects the repo from the URL segment and rewrites the path before endpoint routing matches it.

### Decision required

Should the middleware handle path rewriting internally, or should `Program.cs` map multiple MCP endpoints? **Recommended: single middleware with path rewrite** — simpler, dynamic, no restart needed if repos change.

### How path rewrite works with `MapGroup` endpoint routing

`MapMcp` uses `endpoints.MapGroup(pattern)` (NOT the old `Map()` middleware branching). `MapGroup` is an **endpoint routing** concept — the pattern is a prefix for route matching, not path stripping. Middleware registered via `app.Use*()` runs **before** endpoint routing, so modifying `HttpContext.Request.Path` in middleware is seen by the routing system. This is the same pattern used by `UsePathBase` and URL Rewriting Middleware.

### Scope

- Create `RepoNameDetectionMiddleware` (or extend `RepoScopedMiddleware`)
- Match path pattern `/api/mcp/{repoName}` (simple path segment check)
- Validate `repoName` against configured repositories (from `RepositoriesConfig`), return 404 if unknown
- Set `HttpContext.Items["RepoName"]` to the extracted name
- Rewrite `HttpContext.Request.Path` from `/api/mcp/repo1` to `/api/mcp`
- Call `await next(context)`
- The existing `RepoScopedMiddleware.InvokeAsync` already checks for `HttpContext.Items["RepoName"]` and swaps the provider — keep that logic intact

### Constraints

- Must not break existing `/api/mcp` calls (no repo segment → default behavior)
- Only `POST /api/mcp` is registered in stateless mode — tool names are in the JSON-RPC body, NOT in the URL. No sub-paths like `/tools/list` exist.
- Case-insensitive repo name matching
- The `Repositories` config must be accessible to this middleware. Inject `IConfiguration` or resolve it from the `IServiceProvider` available in middleware constructor.
- **Middleware order**: `Use*` middleware runs before endpoint routing. Our path rewrite happens before `MapGroup` pattern matching, so routing sees the rewritten `/api/mcp` path — this is by design.

### Suggested implementation path

```csharp
// In Program.cs:
app.UseRepoNameDetection();   // new — extracts repo from URL
app.UseRepoScopedMcp();       // existing — swaps RequestServices
app.MapMcp("/api/mcp");        // existing — path already stripped
```

### Acceptance criteria

- `POST /api/mcp/repo1` → `HttpContext.Items["RepoName"]` = `"repo1"`, path rewritten to `/api/mcp`, request forwarded to MCP handler
- `POST /api/mcp` (no repo) → path unchanged, `Items["RepoName"]` not set, works as today
- `POST /api/mcp/unknown-repo` → 404 before reaching MCP handler
- All MCP tools (ping, semantic_search, etc.) work through the repo-prefixed URL
- The rewritten path is only used for endpoint routing; `StreamableHttpHandler` reads the JSON-RPC body and headers, not `Request.Path`

### Files likely involved

- `src/CodeMemory.AspNet/Services/RepoScopedMiddleware.cs` (extend or co-locate)
- `src/CodeMemory.AspNet/Program.cs` (middleware registration order)

---

## Task 4: Wire Multi-Repo in Program.cs

### Priority

High

### Goal

Replace the single-repo path in `Program.cs` with the full multi-repo setup: iterate `Repositories` config, register keyed services (Task 1), plug in detection middleware (Task 3), and remove `MultiRepoServiceFactory` if superseded.

### Why this exists

All the scaffolding is built but `Program.cs` still uses the old single-repo path (`builder.Services.AddCodeMemoryStorage(connectionString)` for the first repo only). This task connects everything.

### Scope

- Remove or conditionalize the single-repo `AddCodeMemoryStorage` call (lines 33-37)
- Move the `NgramEmbeddingGenerator` registration before the repo loop
- Add the repo-iteration + keyed DI block from Task 1
- Register middlewares in correct order: repo detection → scoped provider swap → MCP Map
- Decide fate of `MultiRepoServiceFactory`: keep as legacy compat or remove
- Remove the `firstRepoName`/`repoPath` variables that were used solely for the health endpoint — emit health per-repo instead

### Constraints

- Middleware order matters: detection must run before provider swap, which must run before MCP
- `app.MapMcp("/api/mcp")` must not change — still handles the underlying request after path rewrite
- **SDK behavior**: In stateless mode, `StreamableHttpHandler.CreateSessionAsync` uses `context.RequestServices` (our swapped provider) for per-request service resolution. This means `RepoScopedServices` MUST have the root-provider fallthrough (Task 2) working or `McpServer.Create()` will fail to resolve infrastructure dependencies.
- **Middleware vs endpoint routing**: `Use*` middleware runs before endpoint routing. Registration order is `UseRepoNameDetection` → `UseRepoScopedMcp` → `MapMcp`. The `MapMcp` call registers routes, it does NOT insert middleware — the endpoint routing middleware runs implicitly at the end of the pipeline and matches against whatever `Request.Path` is at that point.

### Acceptance criteria

- App starts with 0, 1, or 2+ repos configured in `appsettings.json:Repositories`
- Each repo gets indexed (existing `IndexingHostedService` behavior preserved)
- `GET /api/mcp/repo1/ping` returns `{"status":"ok"}`
- `GET /api/mcp/repo2/ping` returns `{"status":"ok"}`
- Health endpoint shows all configured repos and their DB paths
- No regressions in single-repo mode (one repo in config)

### Files likely involved

- `src/CodeMemory.AspNet/Program.cs`
- `src/CodeMemory.AspNet/Services/IndexingHostedService.cs` (verify its per-repo scope loop still works)
- `src/CodeMemory.AspNet/Configuration/MultiRepoServiceFactory.cs` (remove or keep)

---

## Task 5: Route Existing Tests Through Multi-Repro Pipeline

### Priority

Medium

### Goal

Instead of writing new integration tests, reuse the existing MCP tool test suite to validate the multi-repo routing. Add a `"test"` repo entry pointing to `.` (relative path), then update all existing test URLs from `/api/mcp` to `/api/mcp/test`. This exercises the URL detection middleware, path rewrite, provider swap, and keyed DI resolution through every existing test.

### Why this approach

Less new code, more coverage. The existing testsmock services at the non-keyed DI layer and call `POST /api/mcp`. By routing them through `/api/mcp/test`, every test validates the full multi-repo pipeline. The key challenge is that `RepoScopedServices` resolves via `GetKeyedService<T>(repoName)`, but mocks are registered as non-keyed singletons via `ConfigureServices`. This is solved by a per-type fallback: try keyed first, then non-keyed, then root fallback.

### Scope

1. **Modify** `RepoScopedServices.GetService()` — per known type, try `GetKeyedService<T>(repoName)` first, then `GetService<T>()` as fallback, so non-keyed mocks resolve correctly.
2. **Test config** — no `appsettings.json` needed in the test project. Use `WebApplicationFactory.WithWebHostBuilder(b => b.UseSetting("Repositories:test", "."))` to add the test repo. The `IndexingHostedService` will try to index the current directory — disable it by adding a `UseSetting` to remove or skip it, or accept the indexing overhead (test output dir is fast/empty).
3. **Update all MCP test URLs** — every `POST /api/mcp` in `src/CodeMemory.Tests/Mcp/*.cs` becomes `POST /api/mcp/test`.
4. **No new test files** — the existing suite becomes the multi-repo validation.
5. **Separate data isolation tests** — optional: a small `MultiRepoTests.cs` can be added later if true 2-repo isolation coverage is needed, but the primary validation comes from the existing suite passing through the new pipeline.

### Implementation details

```csharp
// In RepoScopedServices.GetService():
nameof(IDependencyGraphService) => root.GetKeyedService<IDependencyGraphService>(repoName)
    ?? root.GetService<IDependencyGraphService>(),

// In each test's WebApplicationFactory setup:
await using var factory = new WebApplicationFactory<Program>()
    .WithWebHostBuilder(b =>
    {
        b.UseSetting("Repositories:test", ".");
        b.ConfigureServices(s =>
        {
            // suppress IndexingHostedService for test speed
            var hd = s.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && d.ImplementationType?.Name == "IndexingHostedService");
            if (hd != null) s.Remove(hd);
            s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
        });
    });
```

### Acceptance criteria

- All existing MCP tool tests pass when URLs are changed to `/api/mcp/test`
- `RepoScopedServices` correctly resolves non-keyed mocks (fallback works)
- `GET /api/mcp` (no repo) still works for the `McpInfrastructureTests`
- `GET /api/mcp/nonexistent` returns 404 (test added)
- Indexing is suppressed in tests (no slow file crawling)
- No regressions in unit tests (non-MCP tests unchanged)

### Files likely involved

- `src/CodeMemory.AspNet/Configuration/RepoScopedServices.cs` (non-keyed fallback)
- `src/CodeMemory.Tests/Mcp/*.cs` (URL path update in all 9 files)
- `src/CodeMemory.Tests/Mcp/McpInfrastructureTests.cs` (add 404 test, health check stays at `/health`)

---

## Suggested Agent Handout Batches

### Batch A: decision-critical (can run in parallel)

- Task 1 — Keyed DI registration
- Task 2 — RepoScopedServices fallthrough
- Task 3 — URL detection middleware

### Batch B: integration (requires A)

- Task 4 — Wire everything in Program.cs
- Task 5 — Integration tests

---

## SDK Research Findings (MCP C# SDK v1.1 +)

Add these to your context when working on any task. These were confirmed by reading the `ModelContextProtocol.AspNetCore` source at commit `89fcf3f8`.

| Fact | Detail |
|------|--------|
| `MapMcp` uses `MapGroup` | `endpoints.MapGroup(pattern)` — endpoint routing, NOT middleware `Map()`. No path stripping. |
| Stateless mode routes | Only `POST ""` (i.e., `POST /api/mcp`). No SSE, no `GET`, no `DELETE`, no `/message`. Tool names are in JSON-RPC body, NOT URL. |
| `RequestServices` in stateless | `StreamableHttpHandler.CreateSessionAsync` assigns `mcpServerServices = context.RequestServices`. Our `RepoScopedServices` swap is consumed here. |
| `Request.Path` unused by handler | `HandlePostRequestAsync` reads headers, body, `User`, and `RequestServices` only. Path is irrelevant to handler logic. |
| Infrastructure DI must resolve | `McpServer.Create(transport, options, loggerFactory, mcpServerServices)` receives `mcpServerServices` as its service provider. `ILogger<T>`, `IOptions<T>` etc. must resolve through `RepoScopedServices` or the request pipeline breaks. |
| Constructor vs per-request | `StreamableHttpHandler` constructor (singleton) uses root `IServiceProvider` for `ILoggerFactory`, `IOptions<>`. Per-request `McpServer` uses `context.RequestServices`. Both paths must work. |

---

## Final Checklist

- [ ] every task has a clear owner-sized scope
- [ ] every task has acceptance criteria
- [ ] decision-gate tasks are clearly marked (Task 1 has a decision note about keyed vs shared)
- [ ] likely files are listed to reduce agent search time
- [ ] execution order reflects real dependencies (A → B)
- [ ] Tasks 1-3 are independent and parallelizable
- [ ] SDK findings table is in agent context
