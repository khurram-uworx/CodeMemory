# Enterprise Plan — CodeMemory.AspNet

## What We're Building

A lightweight, self-service ASP.NET host where users register repos via a web UI or static config seed.
Agent-oriented flow: specify a name + source (GitHub URL or local path), the system clones (if URL) + indexes it, and exposes the repo as an MCP endpoint at `/api/mcp/{name}`.

**Constraints:** No auth (externally protected), Razor Pages UI, EF Core registry DB (SQLite OOB, swappable),
full re-index for now.

---

## Phase 1: Registry + Dynamic Registration (Foundation)

### Task 1.1 — RepoRegistry EF Core Context + Model

**Goal:** Persistent store for registered repos, independent of vector stores.

Create `src/CodeMemory.AspNet/Registry/`:

| File | Purpose |
|------|---------|
| `Models/RegisteredRepo.cs` | Entity: Id, Name, GitUrl (nullable, for URL repos), LocalPath (source path for path-based; clone destination for URL-based), Branch (nullable), CloneStatus, IndexStatus, ErrorMessage, CreatedAt, LastIndexedAt |
| `RepoRegistryDbContext.cs` | EF Core DbContext with `DbSet<RegisteredRepo>`, SQLite default, configurable connection |
| `RepoRegistryService.cs` | CRUD operations (List, Get, Add, UpdateStatus, Delete) |
| `Migrations/` | EF Core migrations for SQLite |

**appsettings.json addition:**
```json
{
  "ConnectionStrings": {
    "RepoRegistry": "Data Source=App_Data/registry.db"
  },
  "RepoRegistry": {
    "Provider": "sqlite",
    "CloneBasePath": "./cloned-repos"
  }
}
```

Connection string follows the same pattern as vector store providers: `RepoRegistry:Provider` selects the provider (sqlite default), and the connection string is read from `ConnectionStrings:RepoRegistry`. This keeps config consistent with how `PgVector` / `SqlServer` connection strings are managed for `Storage:Provider`.

`RepoRegistryDbContext` uses a DI-registered `DbContextOptions<RepoRegistryDbContext>` so users can override the provider. Default wired with SQLite in `Program.cs`.

**Acceptance criteria:**
- `RegisteredRepos` table created on startup if not exists
- CRUD operations work via `RepoRegistryService`
- Providers can be swapped via configuration (SQLite default, SQL Server/PostgreSQL overridable)

---

### Task 1.2 — Razor Pages: Dashboard + Add

**Goal:** Web UI for managing registered repos.

| Route | Page | Purpose |
|-------|------|---------|
| `GET /` | `Pages/Index.cshtml` | Dashboard: list all repos with status, MCP endpoint, actions. Two-row entry: master row (name, status badge) + collapsible detail row (source, branch, last indexed, error). Per-entry buttons: Reindex, Delete. |
| `GET /Repos/Add` | `Pages/Repos/Add.cshtml` | Form: repo name + source (GitHub URL or local path) + optional branch |
| `POST /Repos/Add` | (handler) | Validates input: if URL → `CloneStatus=Pending`; if path → resolve, set `LocalPath`, `CloneStatus=Cloned`. Redirects to `/`. |
| `POST /Repos/{name}/Reindex` | (handler) | Trigger re-index manually |
| `POST /Repos/{name}/Delete` | (handler) | De-register: remove from `ServiceRegistry`, delete cloned/indexed files from disk, delete DB row |

Status polling: Page JS polls `/api/repos/{name}/status` per row for live updates during clone/index.

Add Bootstrap 5 via CDN for clean UI without npm complexity.

**Acceptance criteria:**
- Can add a repo via form, see it appear in dashboard list
- Live status updates visible per row during clone/index
- Can trigger re-index and delete from dashboard
- Root `/` still serves the JSON status API (moved to `/api/status` or kept alongside)

---

### Task 1.3 — Background Clone + Index Pipeline

**Goal:** After form submission, clone/index repo, init storage, register MCP endpoint.

Create `CloneIndexService` (singleton, not BackgroundService):

```csharp
public sealed class CloneIndexService
{
    // Fire-and-forget: called by Razor Page handler
    public Task EnqueueRepoAsync(string repoName, string source, string? branch);
    public Task DeleteRepoAsync(string repoName);
}
```

**Fire-and-forget pattern**: Since `CloneIndexService` is a singleton running work on `Task.Run`, do NOT capture scoped services (like `RepoRegistryDbContext`) from the Razor Page request. Instead:
- Inject `IDbContextFactory<RepoRegistryDbContext>` (singleton-safe) — call `CreateDbContext()` inside each background operation for DB access
- Inject `IServiceScopeFactory` for resolving `IndexingEngine` (scoped) — create scope, resolve engine, run, dispose

See: [Use scoped services within a BackgroundService](https://learn.microsoft.com/dotnet/core/extensions/scoped-service) and [DbContext factory](https://learn.microsoft.com/ef/core/dbcontext-configuration/#use-a-dbcontext-factory).

Single `EnqueueRepoAsync` dispatches on source type:
- **URL** (contains `://`): needs clone → go through cloning pipeline
- **Path** (local, absolute or relative): skip clone → go directly to indexing

**URL flow** (`EnqueueRepoAsync` fires `Task.Run`):
1. Update `CloneStatus` → `Cloning`
2. `git clone --branch {branch} {source} {clonePath}`
3. Update `CloneStatus` → `Cloned`, save `LocalPath`
4. Create `IStorageService` for this repo
5. Call `storageRegistry.Register(name, storage)`
6. Update `IndexStatus` → `Indexing`
7. Create `IndexingEngine` via `IServiceScopeFactory`, run `RunIndexingAsync`
8. Mark `IndexingState.MarkCompleted(name)`
9. Update `IndexStatus` → `Indexed`, set `LastIndexedAt`
10. On any error at clone step: update `CloneStatus` → `Failed`; at index step: update `IndexStatus` → `Failed`. Set `ErrorMessage`.

**Path flow** (runs synchronously in the handler or fire-and-forget):
1. Resolve `LocalPath` to full path
2. Update `CloneStatus` → `Cloned` (no clone needed)
3. Create `IStorageService`, register in `storageRegistry`
4. Update `IndexStatus` → `Indexing`
5. Same indexing steps as URL flow (steps 7-10)

**`DeleteRepoAsync`:**
1. Look up repo from DB
2. Remove from `storageRegistry` (need `Unregister` on `IServiceRegistry`)
3. Delete cloned/indexed files from disk (delete `LocalPath` directory)
4. Delete DB row

**Key change in Program.cs:** Remove the per-repo `MapMcp` loop. Add a single catch-all:

```csharp
app.MapMcp("/api/mcp/{repoName}");
```

**Verified:** `MapMcp` internally uses `endpoints.MapGroup(pattern)`, which supports ASP.NET Core route parameters (`{repoName}`). The `[StringSyntax("Route")]` annotation on the pattern parameter confirms it's a route template. Source: `McpEndpointRouteBuilderExtensions.cs` in `ModelContextProtocol.AspNetCore`.

**Update `ConfigureSessionOptions`** to use route values instead of manual path parsing:
```csharp
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
```

`StorageServiceRouter.GetStorage()` returns an error if repo not found.

**Acceptance criteria:**
- Adding a repo clones it to disk
- Storage is registered and MCP endpoint becomes live immediately after cloning
- Indexing runs in background, status updates visible in UI
- Failed clones report error message
- Catch-all MCP route works for all repos

---

### Task 1.4 — Config Seed + Dynamic Bootstrap

**Goal:** Seed `Repositories` config into DB on first run, then load all repos from DB at startup.

`Program.cs` startup bootstrap:
1. Ensure registry DB exists (auto-migrate)
2. If `RegisteredRepos` table is **empty**, seed from `appsettings.json:Repositories`:
   - Each entry: key = name, value = source (URL or local path)
   - If value contains `://` → URL repo: `GitUrl = value`, `CloneStatus = "Pending"`
   - If value is a path → path repo: resolve to full path, `LocalPath = resolved`, `CloneStatus = "Cloned"`
   - All seeded repos get `CreatedAt = UtcNow`
3. Load all repos where `CloneStatus == "Cloned"` from DB
4. For each, create `IStorageService`, register in `ServiceRegistry`, mark `IndexingState` appropriately
5. Register single catch-all `MapMcp("/api/mcp/{repoName}")`
6. **Update `IndexingHostedService`** — currently reads from static `Repositories` config; change to query DB for repos where `(CloneStatus = "Pending" OR IndexStatus = "Pending")`. For each:
   - URL repo (`GitUrl` is set): clone first (CloneStatus → Cloning → Cloned), then index (IndexStatus → Indexing → Indexed)
   - Path repo (`GitUrl` is null): skip clone, index immediately
   - On error: set appropriate status to `Failed`
7. Replace `ConfigureSessionOptions` path-splitting with `context.Request.RouteValues["repoName"]` (see Task 1.3)

**Edge cases:**
- Repos at `Indexing` or `Cloning` on shutdown → reset to `Pending` on startup so they retry
- After seeding, the `Repositories` config section is **no longer consulted directly** — all state lives in DB
- Seeding only happens once (empty table); subsequent startups read from DB only

**Acceptance criteria:**
- First startup: config entries seeded into DB
- Subsequent startups: DB state is source of truth, config changes ignored
- Path-based repos are immediately available; URL-based repos go through clone pipeline
- Catch-all MCP route works for all repos (seeded + user-added)
- Delete removes the repo from DB + disk + ServiceRegistry permanently

---

### Task 1.5 — ASP.NET Program.cs Restructure

**Goal:** Clean up `Program.cs` — it's getting complex with the Razor Pages, registry, and catch-all route.

Extract storage initialization into a `StorageBootstrapper` class. `Program.cs` remains the entry point but delegates:

```
Program.cs
├── Configure services (DI)
│   ├── AddRazorPages()
│   ├── AddRepoRegistry()
│   └── AddMcpServer() + catch-all route
│
├── Build app
├── Seed config → DB (if empty)
├── Load repos from DB → ServiceRegistry
├── UseCors, MapRazorPages, MapMcp, MapGet("/")
└── Run
```

**No functional changes** — pure organizational refactor.

---

<!-- Phase 2 (Nightly Sync) removed — deferred for later implementation -->

## Phase 3: Polish

### Task 3.1 — MCP Re-Index Tool (Uncomment AdminTool)

**Goal:** Allow agents to trigger re-index via MCP instead of needing the web UI.

Uncomment `AdminTool.cs` in `CodeMemory/Mcp/AdminTool.cs`. Implement `RescanRepositoryAsync`:

- Calls `IndexingEngine.RunIndexingAsync` on the current repo
- Clears storage first, then re-indexes
- Returns structured JSON with status

Requires making `IndexingEngine` invocable outside the hosted service scope pattern. Either:
- Register `IndexingEngine` as singleton (instead of scoped) — needs `IStorageService` routing
- Or use `IServiceScopeFactory` inside the tool

**Acceptance criteria:**
- `rescan_repository` MCP tool available in both hosts
- Returns success/error JSON
- Repo is queryable after re-index completes (poll `ping`)

---

### Task 3.2 — Error Handling + Edge Cases

- Duplicate repo name detection (unique constraint on `Name`)
- Git clone timeout (configurable, default 5 min)
- Partial index handling (some files fail → mark partial, don't fail entire repo)
- Disk space check before clone
- Concurrent clone protection (same repo queued twice)

---

### Task 3.3 — Status API Endpoint

**Goal:** Machine-readable status for the UI and external monitoring.

```
GET /api/repos — list all repos with full status
GET /api/repos/{name} — single repo status
GET /api/repos/{name}/status — lightweight status (polled by UI during clone/index)
```

Returns JSON matching `RegisteredRepo` entity minus sensitive fields.

---

## Execution Order

| Step | Task | Depends On |
|------|------|------------|
| 1.1 | RepoRegistry EF Core model + context | — |
| 1.2 | Razor Pages (List, Add, Detail) | 1.1 |
| 1.3 | Clone + Index pipeline | 1.1 |
| 1.4 | Config seed + dynamic bootstrap | 1.3 |
| 1.5 | Program.cs restructure | 1.4 |
| 3.1 | AdminTool uncomment + re-index MCP tool | 1.3 |
| 3.2 | Error handling + edge cases | 1.3 |
| 3.3 | Status API endpoint | 1.1 |

## Files Likely Involved

### New files
- `src/CodeMemory.AspNet/Registry/Models/RegisteredRepo.cs`
- `src/CodeMemory.AspNet/Registry/RepoRegistryDbContext.cs`
- `src/CodeMemory.AspNet/Registry/RepoRegistryService.cs`
- `src/CodeMemory.AspNet/Registry/RepoRegistryOptions.cs`
- `src/CodeMemory.AspNet/Services/CloneIndexService.cs`
- `src/CodeMemory.AspNet/Pages/Index.cshtml` (+ .cshtml.cs)
- `src/CodeMemory.AspNet/Pages/Repos/Add.cshtml` (+ .cshtml.cs)
- `src/CodeMemory.AspNet/Pages/_Layout.cshtml`
- `src/CodeMemory.AspNet/Pages/_ViewStart.cshtml`
- `src/CodeMemory.AspNet/wwwroot/` (Bootstrap, site.css)

### Modified files
- `src/CodeMemory.AspNet/Program.cs`
- `src/CodeMemory.AspNet/appsettings.json`
- `src/CodeMemory.AspNet/CodeMemory.AspNet.csproj`
- `src/CodeMemory.AspNet/Services/IndexingHostedService.cs` (read from DB instead of static config)
- `src/CodeMemory.AspNet/Configuration/ServiceRegistry.cs` (add `Unregister`)
- `src/CodeMemory.AspNet/Configuration/StorageServiceRouter.cs` (handle missing repo)
- `src/CodeMemory/Mcp/AdminTool.cs` (uncomment + wire up)

## Out of Scope (Phase 1)

- Auth/security — external protection
- Incremental indexing — full re-index only
- Edit registered repo — add only, edit deferred
- Custom PgVectorStore cleanup — separate concern
- sql_query convergence — separate concern
- OpenTelemetry — separate concern
- Docker — separate concern

---

## Coordination Notes

- Tasks 1.1 and 1.2 can be built in parallel (model + UI)
- Task 1.3 depends on 1.1 (needs registry to store clone status)
- Task 1.4 (catch-all route) depends on 1.3 — shouldn't deploy catch-all without the dynamic registration pipeline
- Task 3.1 (AdminTool) is independent of the registry — can be done anytime
- Tasks 1.1–1.4 form the critical path; Phase 3 can be reordered based on priority
- Config seed (Task 1.4) is one-time: prevents duplicate handling of static vs dynamic sources
