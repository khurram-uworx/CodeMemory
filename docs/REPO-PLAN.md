# Multi-Repo MCP Support

## Purpose

This plan breaks the multi-repo feature (detect repo name from `/api/mcp/{repoName}`, route to per-repo storage) into concrete, assignable tasks for coding agents.

## Design Summary

**Approach**: `StorageServiceRouter` + `IRepoContextAccessor` (AsyncLocal-based) + thin middleware.

**Why not keyed DI?** The prior approach used keyed DI + `RepoScopedServices` + `RequestServices` swap middleware. That was fragile: every MCP SDK infrastructure dependency (`ILogger<T>`, `IOptions<T>`) had to resolve through a hardcoded switch fallthrough or the request pipeline broke. Adding new services required updating both keyed registrations and the fallthrough switch. Tests required non-keyed fallback hacks.

**This approach avoids the DI swap entirely** by keeping a single non-keyed `IStorageService` that delegates to per-repo storage via ambient context (`IRepoContextAccessor.CurrentRepoName`). All 7 dependent services (`DependencyGraphService`, `ArchitectureService`, etc.) remain non-keyed — they just depend on `IStorageService` which now routes correctly. No `RepoScopedServices`, no keyed DI, no fallthrough bugs.

```
 ┌─────────┐  repo1.memorycode/sqlvec.db
 │  Registry│─repo2─→ StorageService(repo2)
 │  (dict)  │─repo3─→ StorageService(repo3)
 └────┬─────┘
      │lookup by repoName
 ┌────▼──────────┐     ┌──────────────────────┐
 │StorageService │     │ IRpoContextAccessor   │
 │Router         │◄────│ (AsyncLocal repoName) │
 │(IStorageService)    └─────────┬────────────┘
 └──────────────┘               │set by
                                │
 ┌──────────────────────────────▼───┐
 │ RepoRoutingMiddleware (AspNet)    │
 │ extracts /api/mcp/{repoName}     │
 │ rewrites path → /api/mcp         │
 │ sets IRepoContextAccessor         │
 └──────────────────────────────────┘
```

**Key insight**: `IStorageService` is the **only** service that differs per repo. All other services (`DependencyGraphService`, `ArchitectureService`, `GitHistoryService`, etc.) operate on whatever `IStorageService` gives them — they don't need to be per-repo instances. The `GitHistoryService` has a `repoRoot` field, but that comes from config, not DI keying.

**MCP SDK compatibility**: In stateless mode, the SDK uses `HttpContext.RequestServices` directly (no scope creation — `ScopeRequests` is `false`). We do NOT swap `RequestServices`. The MCP SDK resolves `ILogger<T>`, `IOptions<T>`, etc. from the standard ASP.NET request pipeline — no fallthrough code needed.

---

## Tasks

1. **Task 1** — `StorageServiceRegistry` + `StorageServiceRouter` + `IRepoContextAccessor`
2. **Task 2** — URL-based repo detection middleware (with path rewrite)
3. **Task 3** — Wire multi-repo in Program.cs + remove old scaffolding
4. **Task 4** — Tests
5. **Task 5** — Update public docs (ARCHITECTURE.md, README.md, AGENTS.md)

## Coordination Notes

- Tasks 1-2 can be done in parallel (different files).
- Task 3 must wait for Tasks 1-2.
- Task 4 can start after Task 3 is testable (depends on full pipeline).
- Task 5 is last — run after all other tasks are merged.

---

## Task 1: StorageServiceRegistry + StorageServiceRouter + IRepoContextAccessor

### Priority

High

### Goal

Create the infrastructure for per-repo storage routing: a registry of per-repo `IStorageService` instances, a router that delegates based on ambient repo context, and an `AsyncLocal`-based context accessor.

### Why this exists

Currently `Program.cs:37` calls `builder.Services.AddCodeMemoryStorage(connectionString)` which registers a single `IStorageService`. For multi-repo, we need one `IStorageService` per repo (each pointing at a different SQLite DB). The router pattern lets all other services stay non-keyed while `IStorageService` correctly delegates per request.

### Scope

1. **`IRepoContextAccessor` / `RepoContextAccessor`** — change to `AsyncLocal<string?>` based ambient context so it can be a singleton and work across async boundaries without DI scopes. Already exists in `src/CodeMemory.AspNet/Configuration/RepoContextAccessor.cs` — replace the simple property fields with `AsyncLocal<string?>`.

2. **`IStorageServiceRegistry`** — new interface in `CodeMemory.AspNet`:
   ```csharp
   public interface IStorageServiceRegistry
   {
       void Register(string repoName, IStorageService storage);
       IStorageService GetStorage(string? repoName);
   }
   ```
   - `GetStorage(null)` returns the first/only registered storage (for single-repo fallback).
   - `GetStorage("repo1")` returns the named storage or throws.
   - Thread-safe (`ConcurrentDictionary`).

3. **`StorageServiceRouter`** — new class in `CodeMemory.AspNet`, implements `IStorageService`:
   ```csharp
   public sealed class StorageServiceRouter : IStorageService
   {
       readonly IStorageServiceRegistry registry;
       readonly IRepoContextAccessor repoContext;

       IStorageService GetStorage() =>
           registry.GetStorage(repoContext.CurrentRepoName);

       // delegates all IStorageService methods to GetStorage()
   }
   ```
   Delegates every `IStorageService` method — there are ~15 methods. Use a private helper `GetStorage()` called at the top of each method.

4. **Remove `MultiRepoServiceFactory`** — superseded by `IStorageServiceRegistry`. Check for any remaining callers first (grep for `IMultiRepoServiceFactory`).

### Constraints

- `IRepoContextAccessor` must be a singleton (`AsyncLocal` naturally supports this).
- `StorageServiceRouter` must be a singleton (same lifetime as other `IStorageService` registrations).
- `IStorageServiceRegistry` must be thread-safe (background indexing and HTTP requests can run concurrently).
- `GetStorage(null)` must return the single storage when only one is registered (for backward compat with single-repo mode).
- `GetStorage(null)` should prefer "default" key if present, else first registered, else throw clear message.

### Files involved

- `src/CodeMemory.AspNet/Configuration/RepoContextAccessor.cs` (modify — AsyncLocal)
- `src/CodeMemory.AspNet/Configuration/StorageServiceRegistry.cs` (new)
- `src/CodeMemory.AspNet/Configuration/StorageServiceRouter.cs` (new)
- `src/CodeMemory.AspNet/Configuration/MultiRepoServiceFactory.cs` (remove)

---

## Task 2: URL-Based Repo Detection Middleware

### Priority

High

### Goal

Create a middleware that extracts the repo name from `/api/mcp/{repoName}`, validates it, rewrites the path to `/api/mcp` (for endpoint routing), and sets `IRepoContextAccessor.CurrentRepoName`.

### Why this exists

`MapMcp("/api/mcp")` registers endpoints at that exact prefix using `MapGroup`. A request to `POST /api/mcp/repo1` does NOT match `POST ""` on the `/api/mcp` group — the trailing `repo1` segment causes a routing mismatch. The middleware rewrites the path so the MCP endpoint matches, while capturing the repo name in ambient context for `StorageServiceRouter`.

### What changed from the old plan

The old plan had two middlewares — one for detection + path rewrite, another for `RequestServices` swap. The swap middleware is gone. This single middleware:
- Does NOT swap `HttpContext.RequestServices` (no `RepoScopedServices`)
- Does NOT use `HttpContext.Items` (uses `IRepoContextAccessor` directly)
- ONLY does detection + validation + path rewrite + ambient context

### Scope

1. Create `RepoRoutingMiddleware` (replace/extend existing `RepoScopedMiddleware`).
2. Match URL pattern `/api/mcp/{repoName}` — simple path segment check after `/api/mcp/`.
3. Validate `repoName` against configured repositories, return 404 if unknown.
4. Set `IRepoContextAccessor.CurrentRepoName` to the extracted name.
5. Rewrite `HttpContext.Request.Path` from `/api/mcp/repo1` to `/api/mcp`.
6. Clean up `IRepoContextAccessor.CurrentRepoName` in `finally` block.
7. Extension method `UseRepoRouting()` for `Program.cs`.
8. Remove `UseRepoScopedMcp` extension and `RepoScopedMiddleware` class (superseded).

### Constraints

- Must not break `/api/mcp` without repo segment — pass through unchanged, current state of `IRepoContextAccessor` preserved.
- Only `POST /api/mcp` is registered in stateless mode — tool names are in JSON-RPC body, not URL.
- Case-insensitive repo name matching.
- `IRepoContextAccessor` is injected in middleware constructor (it's a singleton).
- The `Repositories` config must be accessible — inject `IStorageServiceRegistry` and call its `GetStorage(name)` to validate.
- `IRepoContextAccessor` cleanup in `finally` is critical to prevent context leaking across requests.

### Implementation sketch

```csharp
public sealed class RepoRoutingMiddleware
{
    readonly RequestDelegate next;
    readonly IRepoContextAccessor repoContext;
    readonly IStorageServiceRegistry registry;

    public RepoRoutingMiddleware(RequestDelegate next,
        IRepoContextAccessor repoContext, IStorageServiceRegistry registry)
    {
        this.next = next;
        this.repoContext = repoContext;
        this.registry = registry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        const string prefix = "/api/mcp/";

        if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var repoName = path[prefix.Length..];
            if (string.IsNullOrEmpty(repoName) || repoName.Contains('/'))
            {
                await next(context);
                return;
            }

            // Validate repo exists (will throw if unknown — let 500 propagate, or catch for 404)
            try { registry.GetStorage(repoName); }
            catch { context.Response.StatusCode = 404; return; }

            repoContext.CurrentRepoName = repoName;
            context.Request.Path = "/api/mcp";
            try { await next(context); }
            finally { repoContext.CurrentRepoName = null; }
        }
        else
        {
            await next(context);
        }
    }
}
```

### Acceptance criteria

- `POST /api/mcp/repo1` → `IRepoContextAccessor.CurrentRepoName` = `"repo1"`, path rewritten to `/api/mcp`, request forwarded to MCP handler
- `POST /api/mcp` (no repo) → path unchanged, `IRepoContextAccessor.CurrentRepoName` unchanged, works as today
- `POST /api/mcp/unknown-repo` → 404 before reaching MCP handler
- All MCP tools work through repo-prefixed URL (storage routes to correct DB)
- Cleanup: `CurrentRepoName` is null after request completes

### Files involved

- `src/CodeMemory.AspNet/Services/RepoScopedMiddleware.cs` (replace with `RepoRoutingMiddleware`)
- `src/CodeMemory.AspNet/Program.cs` (registration order — add `UseRepoRouting()`)

---

## Task 3: Wire Multi-Repo in Program.cs

### Priority

High

### Goal

Replace the single-repo path in `Program.cs` with the full multi-repo setup: iterate `Repositories` config, create per-repo storage instances, register `StorageServiceRouter`, plug in middleware.

### Why this exists

Currently `Program.cs` uses the first repo only. This task connects all the pieces from Tasks 1-2.

### Scope

1. Move `IEmbeddingGenerator` registration before the repo loop (repo-agnostic).
2. Read `Repositories` config section at startup.
3. Create `StorageServiceRegistry`, populate with per-repo `IStorageService` instances.
4. Register `StorageServiceRegistry` + `StorageServiceRouter` as singletons.
5. If no repos configured, register a single storage under key `"default"` (backward compat).
6. Replace `app.UseRepoScopedMcp()` with `app.UseRepoRouting()`.
7. Update `/health` endpoint to show all configured repos and their DB paths.
8. Remove `firstRepoName`/`repoPath` variables (were used solely for health endpoint).
9. Remove `MultiRepoServiceFactory` reference if present.
10. Verify `IndexingHostedService` loop still works — it should set `IRepoContextAccessor.CurrentRepoName` before resolving `IndexingEngine` in each iteration (see below).

### IndexingHostedService changes

`IndexingHostedService` must set `IRepoContextAccessor.CurrentRepoName` before creating each indexing scope, so `StorageServiceRouter` delegates to the correct DB:

```csharp
foreach (var (name, path) in repositories)
{
    var repoPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(appBasePath, path));
    repoContext.CurrentRepoName = name;
    repoContext.CurrentRepoRoot = repoPath;

    using var scope = serviceProvider.CreateScope();
    var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
    await engine.RunIndexingAsync(repoPath, stoppingToken);
}
```

No `finally` block needed between iterations — value is overwritten on next iteration. Add a `finally` after the loop to reset to null for cleanliness.

### Constraints

- Middleware order in pipeline: `UseRepoRouting()` must come before `MapMcp()`.
- `StorageServiceRouter` must be registered as the single `IStorageService` — no other `AddCodeMemoryStorage` call.
- `StorageServiceRegistry` must be populated before `StorageServiceRouter` is used (during startup only).
- `IndexingHostedService` already iterates all repos — only needs the `IRepoContextAccessor` wiring added.
- `NgramEmbeddingGenerator`, `FileCrawler`, parsers, chunker, and query services stay as non-keyed singletons — they don't change.

### Implementation sketch (Program.cs)

```csharp
// Register repo-agnostic services first
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();

// Build per-repo storage registry
var repositories = builder.Configuration.GetSection("Repositories").Get<Dictionary<string, string>>();
var storageRegistry = new StorageServiceRegistry();

if (repositories is { Count: > 0 })
{
    var provider = "sqlvec";
    foreach (var (name, path) in repositories)
    {
        var repoRoot = Path.IsPathRooted(path) ? path
            : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
        var memoryPath = Path.Combine(repoRoot, ".memorycode");
        Directory.CreateDirectory(memoryPath);
        var connStr = $"Data Source={Path.Combine(memoryPath, $"{provider}.db")}";
        // Create SQLite VectorStore + StorageService
        // ... factory code ...
        storageRegistry.Register(name, storageService);
    }
}
else
{
    // Single-repo fallback: register default storage from current directory
    var memoryPath = Path.Combine(Environment.CurrentDirectory, ".memorycode");
    Directory.CreateDirectory(memoryPath);
    var connStr = $"Data Source={Path.Combine(memoryPath, "sqlvec.db")}";
    // ... create storage ...
    storageRegistry.Register("default", storageService);
}

builder.Services.AddSingleton<IStorageServiceRegistry>(storageRegistry);
builder.Services.AddSingleton<IRepoContextAccessor, RepoContextAccessor>();
builder.Services.AddSingleton<IStorageService, StorageServiceRouter>();

// ... rest of registrations (query services, architecture, etc.) unchanged ...
// ... middleware: app.UseRepoRouting(); app.MapMcp("/api/mcp"); ...
```

### Acceptance criteria

- App starts with 0, 1, or 2+ repos configured in `appsettings.json:Repositories`.
- Each repo gets indexed to its own `.memorycode/sqlvec.db`.
- `POST /api/mcp/repo1` correctly uses repo1's storage.
- `POST /api/mcp/repo2` correctly uses repo2's storage.
- `POST /api/mcp` (no repo) uses the single default storage (or first repo).
- Health endpoint shows all configured repos and their DB paths.
- `StorageServiceRouter` is the only `IStorageService` registration — no duplicate.
- `MultiRepoServiceFactory` is removed (no remaining references).

### Files involved

- `src/CodeMemory.AspNet/Program.cs`
- `src/CodeMemory.AspNet/Services/IndexingHostedService.cs` (add `IRepoContextAccessor` wiring)

---

## Task 4: Tests

### Priority

Medium

### Goal

Verify the multi-repo pipeline works without changing most existing test URLs or mock patterns. Unlike the old plan (which required updating all 9 test files + adding non-keyed fallback hacks), this approach is backward-compatible with existing tests.

### Why this approach is simpler now

The old approach used keyed DI + `RepoScopedServices` — tests had to route through `/api/mcp/test` to hit the keyed services, and mocks registered as non-keyed needed special fallback logic. The new approach keeps a single non-keyed `IStorageService`. Tests that mock at the service interface level (`ISemanticSearchService`, `IDependencyGraphService`, etc.) via `ConfigureServices` work unchanged. Tests don't need URL changes.

### Scope

1. **Existing MCP tool tests** — no URL changes needed. Mocks registered via `ConfigureServices` override the real DI registrations as before. `StorageServiceRouter` is replaced when a test registers a mock `IStorageService`, or the real router delegates to the registry's default storage.

2. **`McpInfrastructureTests`** — keep `/api/mcp` test as-is. Add:
   - One test for `/api/mcp/test` with `UseSetting("Repositories:test", ".")` to validate the routing middleware + `StorageServiceRouter` path.
   - One test for `/api/mcp/nonexistent` expecting 404.

3. **Test setup** — `WebApplicationFactory<Program>()` with no `Repositories` config creates a default storage (current directory). For multi-repo tests, use `WithWebHostBuilder(b => b.UseSetting("Repositories:test", "."))`.

4. **Indexing suppression** — when tests don't configure repos, `IndexingHostedService` logs a warning and returns (existing behavior). When tests add a repo, suppress indexing by removing `IndexingHostedService` from the service collection in `ConfigureServices`.

5. **Optional** — a `MultiRepoTests.cs` that registers two repos and verifies isolation (write to repo1, search in repo2, verify no cross-contamination).

### Why no URL changes needed in existing tests

Existing tests register mocks like:
```csharp
b.ConfigureServices(s => s.AddSingleton<ISemanticSearchService>(new MockSemanticSearchService()));
```

These mocks replace the real service completely in DI. The test calls `POST /api/mcp` which hits the MCP handler. The MCP handler resolves `ISemanticSearchService` — gets the mock. No `StorageServiceRouter` or `IRepoContextAccessor` interaction because `ISemanticSearchService` is mocked, not `IStorageService`.

Even for tests that DON'T mock (like `ArchitectureOverviewToolTests.ReturnsDefault_WhenNoService`), the real `IArchitectureService` resolves, which depends on `IStorageService` — which is `StorageServiceRouter`. With no repo in config, `StorageServiceRouter` gets null from `IRepoContextAccessor.CurrentRepoName`, and `IStorageServiceRegistry.GetStorage(null)` returns the default/first storage. This works.

### Acceptance criteria

- All existing tests pass without URL changes or fallback hacks.
- `POST /api/mcp/test` works through the middleware + router pipeline.
- `POST /api/mcp/nonexistent` returns 404.
- Indexing is suppressed in tests that add repos (no slow file crawling).
- No regressions in unit tests (non-MCP tests unchanged).

### Files involved

- `src/CodeMemory.Tests/Mcp/McpInfrastructureTests.cs` (add multi-repo + 404 tests)
- `src/CodeMemory.Tests/Mcp/MultiRepoTests.cs` (optional, new)

---

## Task 5: Update Public Docs

### Priority

Medium

### Goal

Update `ARCHITECTURE.md`, `README.md`, and `AGENTS.md` to reflect the new multi-repo architecture and remove references to the old keyed DI approach.

### Why this exists

The design changed significantly — old docs reference `RepoScopedServices`, keyed DI, and patterns that no longer exist. Coding agents need accurate guidance.

### Scope

1. **`ARCHITECTURE.md`** (repo root, not `docs/`):
   - Add multi-repo section describing `StorageServiceRouter` + `IRepoContextAccessor` + middleware.
   - Update project structure diagram (note `RepoRoutingMiddleware`, `StorageServiceRouter`, `StorageServiceRegistry` in `CodeMemory.AspNet/Configuration/`).
   - Remove references to keyed DI, `RepoScopedServices`, `MultiRepoServiceFactory`.
   - Add MCP data flow diagram for multi-repo: HTTP request → middleware → path rewrite → ambient context → storage routing.
   - Note that `CodeMemory` library has no HTTP dependencies — all routing logic is in `CodeMemory.AspNet`.

2. **`README.md`**:
   - Document multi-repo URL pattern: `POST /api/mcp/{repoName}`.
   - Show `appsettings.json` configuration with multiple repos.
   - Update quick-start to show multi-repo setup.
   - Document that stdio mode uses single-repo (first configured, or "default").

3. **`AGENTS.md`**:
   - Replace old "MCP-First Design" section with accurate patterns.
   - Remove: keyed DI, `RepoScopedServices`, `MultiRepoServiceFactory`, root-provider fallthrough.
   - Add: `StorageServiceRouter`, `IRepoContextAccessor`, middleware-only routing.
   - Update the project structure description to show correct namespaces and files.

4. **Fix path**: Plan preamble says `docs/ARCHITECTURE.md` but it's at repo root `ARCHITECTURE.md` — correct this reference.

### Files involved

- `ARCHITECTURE.md` (repo root)
- `README.md`
- `AGENTS.md`
- `docs/REPO-PLAN.md` (self — update ARCHITECTURE.md reference)

---

## Suggested Agent Handout Batches

### Batch A: can run in parallel

- Task 1 — StorageServiceRegistry + StorageServiceRouter + IRepoContextAccessor
- Task 2 — URL detection middleware

### Batch B: sequential (requires A)

- Task 3 — Wire Program.cs + IndexingHostedService (depends on Task 1 + 2)
- Task 4 — Tests (depends on Task 3 being stable)

### Batch C: final

- Task 5 — Public docs (run after all other tasks merged)

---

## SDK Research (MCP C# SDK v1.3+)

Add these to your context when working on any task.

### How MapMcp works

`MapMcp("/api/mcp")` internally calls `endpoints.MapGroup("/api/mcp")` — endpoint routing, NOT middleware `Map()`. The group registers `POST ""` as the only route in stateless mode. Tool names are in the JSON-RPC body, not the URL.

### Stateless mode service resolution

In stateless mode (`Stateless = true`), the SDK sets `ScopeRequests = false` and uses `HttpContext.RequestServices` as the service provider. The SDK does NOT create additional DI scopes. Tool handlers resolve from the same provider as middleware and ASP.NET Core components.

This means: our middleware does NOT need to swap `RequestServices`. The MCP SDK's `StreamableHttpHandler` resolves `ILogger<T>`, `IOptions<T>`, etc. from the standard ASP.NET pipeline — no fallthrough code needed. This is the key fact that makes the old `RepoScopedServices` approach unnecessary.

### ClaimsPrincipal injection (for reference)

The SDK supports `ClaimsPrincipal? user` parameter injection in tool methods — the parameter is excluded from the JSON schema and auto-injected from the current request context. This pattern proves the SDK has a mechanism for per-request context without HTTP-coupling, but we don't use it for storage routing (we use `IRepoContextAccessor` instead).

### ConfigureSessionOptions (per-request callback)

In stateless mode, `ConfigureSessionOptions` runs on every HTTP request with access to `HttpContext`. It can customize `McpServerOptions.ToolCollection` per request. Not needed for our approach since `StorageServiceRouter` handles per-repo routing at the storage level, not the tool level.

### Key difference from the old approach

Old approach assumed MCP SDK infrastructure resolution (`ILogger<T>`, etc.) would fail through a swapped `RequestServices`. The SDK docs confirm that in stateless mode, `RequestServices` IS the ASP.NET pipeline — no swapping required. This validates the new approach.

---

## Final Checklist

- [ ] every task has a clear owner-sized scope
- [ ] every task has acceptance criteria
- [ ] likely files are listed to reduce agent search time
- [ ] execution order reflects real dependencies (A → B → C)
- [ ] no keyed DI, no RepoScopedServices, no fallthrough switch
- [ ] `IStorageService` is the only per-repo concern — all other services stay non-keyed
- [ ] `IRepoContextAccessor` uses AsyncLocal — singleton-safe, no scoped DI needed
- [ ] middleware does NOT swap RequestServices — MCP SDK uses ASP.NET pipeline directly
- [ ] existing tests pass without URL changes or fallback hacks
- [ ] public docs updated to reflect new architecture
