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

`RepoScopedServices.GetService()` returns `null` for any type not in its hardcoded switch list (`nameof(IStorageService)`, `nameof(ISemanticSearchService)`, etc.). When the middleware swaps `context.RequestServices` to `RepoScopedServices`, the MCP framework tries to resolve loggers, `IOptions<McpServerOptions>`, and other DI infrastructure through this provider. Returning `null` for those breaks the request pipeline.

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

`app.MapMcp("/api/mcp")` binds to a fixed path. To support `/api/mcp/repo1` and `/api/mcp/repo2`, we need either multiple `MapMcp` calls (clunky, requires knowing all repos at startup) or a middleware that detects the repo from the URL segment and strips it from the path.

### Decision required

Should the middleware handle path rewriting internally, or should `Program.cs` map multiple MCP endpoints? **Recommended: single middleware with path rewrite** — simpler, dynamic, no restart needed if repos change.

### Scope

- Create `RepoNameDetectionMiddleware` (or extend `RepoScopedMiddleware`)
- Match path pattern `/api/mcp/{repoName}` (regex or `PathString` segment check)
- Validate `repoName` against configured repositories (from `RepositoriesConfig`), return 404 if unknown
- Set `HttpContext.Items["RepoName"]` to the extracted name
- Rewrite `HttpContext.Request.Path` from `/api/mcp/repo1` to `/api/mcp`
- Call `await next(context)`
- The existing `RepoScopedMiddleware.InvokeAsync` already checks for `HttpContext.Items["RepoName"]` and swaps the provider — keep that logic intact

### Constraints

- Must not break existing `/api/mcp` calls (no repo segment → default behavior)
- Must handle both `/api/mcp/repo1` and `/api/mcp/repo1/tools/list` (sub-paths)
- Must also handle `/api/mcp/repo1/sse` if SSE mode is used
- Case-insensitive repo name matching

### Suggested implementation path

```csharp
// In Program.cs:
app.UseRepoNameDetection();   // new — extracts repo from URL
app.UseRepoScopedMcp();       // existing — swaps RequestServices
app.MapMcp("/api/mcp");        // existing — path already stripped
```

### Acceptance criteria

- `GET /api/mcp/repo1/tools/list` → `HttpContext.Items["RepoName"]` = `"repo1"`, path rewritten to `/api/mcp/tools/list`
- `POST /api/mcp/repo1` → path rewritten to `/api/mcp`, repo name set
- `GET /api/mcp` (no repo) → path unchanged, `Items["RepoName"]` not set, works as today
- `GET /api/mcp/unknown-repo` → 404
- All MCP tools (ping, semantic_search, etc.) work through the repo-prefixed URL

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

## Task 5: Integration Tests

### Priority

Medium

### Goal

Write integration tests that verify the full multi-repo flow: two repos configured, independent MCP calls routed to correct service instances, correct data isolation.

### Why this exists

The multi-repo feature spans middleware, DI, and service behavior. Unit-level tests miss the cross-cutting wiring. Integration tests catch routing, provider swap, and data isolation bugs.

### Scope

- Create `MultiRepoTests.cs` in `src/CodeMemory.Tests`
- Use `WebApplicationFactory<Program>` with a custom `appsettings.json` that defines two repos pointing at temp directories
- Seed each repo with different data (different symbols/files)
- Test:
  - `GET /api/mcp/repo1/semantic_search?query=foo` returns only repo1's results
  - `GET /api/mcp/repo2/semantic_search?query=foo` returns only repo2's results
  - `GET /api/mcp/repo1/get_architecture_overview` returns repo1's components
  - `GET /api/mcp/repo2/get_architecture_overview` returns repo2's components
  - `GET /api/mcp` (no repo) works with default behavior
  - `GET /api/mcp/nonexistent` returns 404
- Mock or disable indexing for test speed (`IndexingHostedService` can be suppressed via config with 0 repos)

### Suggested implementation path

```csharp
[Test]
public async Task Repo1_and_Repo2_have_independent_data()
{
    await using var factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.UseSetting("Repositories:repo1", path1)
                                   .UseSetting("Repositories:repo2", path2));
    var client = factory.CreateClient();
    // ... seed repo1 with symbol "Foo", repo2 with symbol "Bar" ...
    // ... call MCP tools for each and verify isolation ...
}
```

### Acceptance criteria

- All tests pass
- Data isolation is confirmed: repo1's data never leaks into repo2's responses
- Tests run in CI without external dependencies
- `IndexingHostedService` can be cheaply skipped in test (e.g., empty repos config or test hook)

### Files likely involved

- `src/CodeMemory.Tests/MultiRepoTests.cs` (new)
- `src/CodeMemory.Tests/TestConfigs/` (optional, for shared appsettings.json)

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

## Final Checklist

- [ ] every task has a clear owner-sized scope
- [ ] every task has acceptance criteria
- [ ] decision-gate tasks are clearly marked (Task 1 has a decision note about keyed vs shared)
- [ ] likely files are listed to reduce agent search time
- [ ] execution order reflects real dependencies (A → B)
- [ ] Tasks 1-3 are independent and parallelizable
