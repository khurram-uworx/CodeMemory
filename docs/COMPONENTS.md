# Component Management — Phase 4

## Purpose

Phase 4 delivers a complete component management experience: refined data model (enums, file count, soft delete), smart upsert logic that respects user edits as source of truth, API endpoints, and a Repo Detail UI page for viewing, editing, and deleting components.

Phase 1–3 (interface rename, storage wiring, detection enrichment) is already implemented in the previous session.

## How To Use

Tasks are ordered by dependency. Each task is small enough for one coding agent. Shared files are noted to avoid merge conflicts.

## Suggested Execution Order

1. Task 1: Rename + Enums (prerequisite for everything else)
2. Task 2: Soft Delete + Merge Logic (prerequisite for API + UI)
3. Task 3: File Count Plumbing (can merge with Task 2)
4. Task 4: API Endpoints (depends on Tasks 1–3)
5. Task 5: Repo Detail UI Page (depends on Task 4)
6. Task 6: Tests (can start after Tasks 1–3, finalize after Task 5)

## Coordination Notes

- **Task 1 touches ~15 files** — expect merge conflicts with any parallel branch.
- **Tasks 2 and 3 both modify `HybridStorageService.cs`** — do them sequentially, not in parallel.
- **Task 4 and Task 5** depend on Tasks 1–3 being done.
- **Task 6** can begin in parallel with Tasks 4–5 using unit-level tests, but integration tests need the API in place.
- **`StorageService` (Mcp, in-memory)** is intentionally unchanged — it has no UI, no soft delete, and keeps simple replace-all semantics.

## Task 1: Rename `ComponentMappingInfo` → `ComponentInformation` + Introduce Enums

### Priority

High

### Goal

Rename the existing `ComponentMappingInfo` record to `ComponentInformation` and introduce `ComponentKind` and `ComponentType` enums.

### Why this exists

The old name and string-based fields were designed for a simple key-value mapping. The enriched model needs proper types for UI dropdowns and data integrity.

### Scope

- Rename `ComponentMappingInfo` record to `ComponentInformation` across all files
- Create `ComponentKind` enum (`Unknown=0, MsBuild, Maven, Node, Cargo, Go, Gradle, Python, CMake, Haskell, Folder`)
- Create `ComponentType` enum (`Component=0, Test, Tool, Documentation, Example, Other`)
- Update `ComponentInformation` to use enum types
- Update `ProjectFileDetector.KnownBuildFiles` tuples from `(string, string)` to `(string, ComponentKind)`
- Update all callers: `IStorageService`, `StorageService`, `ComponentResolver`, `IndexingEngine`, `HybridStorageService`, `StorageServiceRouter`, `ServiceCollectionExtensions`, `StorageBootstrapper`, `MockServices`, tests
- Update `ComponentEntity` EF Core configuration to store enums as strings

### Constraints

- `StorageService` (Mcp) keeps `ConcurrentDictionary<string, ComponentInformation>` with simple replace-all — no enums-in-DB concern there.
- Enums stored as strings in EF Core for readability and to avoid migration issues when adding new values.
- The existing `CodeMemory.Indexing.Architecture.ComponentInfo` (Name/FileCount/SymbolCount) must not be confused with the new `ComponentInformation`.

### Suggested implementation path

1. Create `src/CodeMemory/Storage/ComponentKind.cs`
2. Create `src/CodeMemory/Storage/ComponentType.cs`
3. Update `Models.cs`: rename record, change field types to enums
4. Update `IStorageService.cs`: rename type
5. Update `StorageService.cs`: rename type
6. Update `ProjectFileDetector.cs`: change KnownBuildFiles to use `ComponentKind` enum, tuple pattern
7. Update `ComponentResolver.cs`, `IndexingEngine.cs`: rename type
8. Update `HybridStorageService.cs`, `StorageServiceRouter.cs`, `ServiceCollectionExtensions.cs`, `StorageBootstrapper.cs`: rename type
9. Update `ComponentEntity.cs` + `RepoRegistryDbContext.cs`: store enums as strings via EF Core conversion
10. Update `MockServices.cs`, test files: rename type
11. Build and fix any remaining references

### Acceptance criteria

- Solution builds with zero errors
- `ComponentKind` and `ComponentType` enums exist with all expected values
- `ComponentInformation` record uses the enum types
- `ProjectFileDetector.Discover()` returns `IReadOnlyList<ComponentInformation>` with correct enum values
- All 399+ existing tests pass
- EF Core stores enums as human-readable strings in the DB

### Files likely involved

- `src/CodeMemory/Storage/Models.cs`
- `src/CodeMemory/Storage/ComponentKind.cs` (new)
- `src/CodeMemory/Storage/ComponentType.cs` (new)
- `src/CodeMemory/Storage/IStorageService.cs`
- `src/CodeMemory/Storage/StorageService.cs`
- `src/CodeMemory/Services/Architecture/ProjectFileDetector.cs`
- `src/CodeMemory/Services/Architecture/ComponentResolver.cs`
- `src/CodeMemory/Services/IndexingEngine.cs`
- `src/CodeMemory.AspNet/Registry/ComponentEntity.cs`
- `src/CodeMemory.AspNet/Registry/RepoRegistryDbContext.cs`
- `src/CodeMemory.AspNet/Storage/HybridStorageService.cs`
- `src/CodeMemory.AspNet/Configuration/StorageServiceRouter.cs`
- `src/CodeMemory.AspNet/Configuration/StorageBootstrapper.cs`
- `src/CodeMemory.AspNet/Storage/ServiceCollectionExtensions.cs`
- `tests/CodeMemory.Tests/MockServices.cs`
- `tests/CodeMemory.Tests/Storage/HybridStorageServiceTests.cs`

---

## Task 2: Soft Delete + Smart Upsert Merge Logic

### Priority

High

### Goal

Replace the delete-all + insert approach in `HybridStorageService.StoreComponentMappingAsync` with a smart upsert that treats the database as source of truth for existing rows. Add soft-delete support.

### Why this exists

The current implementation destroys user edits on every re-index. If a user changes a component's Kind or Type via the UI, the next index would overwrite it. Soft delete lets users permanently dismiss falsely detected components without them reappearing.

### Scope

- Add `IsDeleted` (bool, default false) and `DeletedAt` (DateTime?, default null) to `ComponentEntity`
- Add EF Core column mappings for new fields in `RepoRegistryDbContext.OnModelCreating`
- Rewrite `HybridStorageService.StoreComponentMappingAsync`:
  - Load existing + soft-deleted entries for the repo → lookup by `BuildFilePath`
  - For each detected component:
    - Not in lookup → insert new
    - In lookup, `IsDeleted == false` → skip (keep user's edits)
    - In lookup, `IsDeleted == true` → skip (don't re-add)
  - Never delete or overwrite existing rows
- `HybridStorageService.LoadComponentMappingAsync` should exclude soft-deleted entries by default
- `StorageService` (Mcp) stays unchanged — simple replace-all is fine for in-memory with no UI

### Constraints

- No hard delete from `StoreComponentMappingAsync` — deletion is always through the UI/API only
- If a build file directory is renamed, the old soft-deleted entry won't match the new path, so a new entry is created. Expected behavior.
- ConcurrentDictionary in `StorageService` has no soft-delete concept — it's ephemeral.

### Suggested implementation path

1. Add `IsDeleted` and `DeletedAt` properties to `ComponentEntity`
2. Add column mappings in `RepoRegistryDbContext.OnModelCreating`
3. Rewrite `HybridStorageService.StoreComponentMappingAsync` with the new upsert logic
4. Update `LoadComponentMappingAsync` to filter out soft-deleted entries
5. Build and verify tests pass

### Acceptance criteria

- First index: all detected components inserted
- User edit via API/UI: change persisted, survives re-index
- User delete via API/UI: `IsDeleted = true`, component disappears, does not reappear on re-index
- Soft-deleted components are excluded from `LoadComponentMappingAsync` results
- `ComponentResolver` does not resolve paths to soft-deleted components
- Existing tests pass; new tests verify user-edit persistence and soft-delete round-trip

### Files likely involved

- `src/CodeMemory.AspNet/Registry/ComponentEntity.cs`
- `src/CodeMemory.AspNet/Registry/RepoRegistryDbContext.cs`
- `src/CodeMemory.AspNet/Storage/HybridStorageService.cs`
- `tests/CodeMemory.Tests/Storage/HybridStorageServiceTests.cs`

---

## Task 3: File Count Plumbing

### Priority

Medium

### Goal

Add `FileCount` to the component data model and compute it during indexing so the UI can display how many files belong to each component.

### Why this exists

A component with 500 files is meaningfully different from one with 5. File count gives users a quick visual cue of component size and scope.

### Scope

- Add `FileCount` (int, default 0) to `ComponentInformation` record and `ComponentEntity`
- In `IndexingEngine.RunIndexingAsync`, after crawling all files, group files by component (using `ComponentResolver.GetComponentNameAsync`), count per group, and pass into `StoreComponentMappingAsync`
- Update `ProjectFileDetector.Discover()` return type to accept a `FileCount` parameter (or let the caller fill it in)
- Update EF Core mapping for new column

### Constraints

- File count is computed per index run. It does not need to be perfectly accurate in between indexes (the count shown is "as of last index").
- The `StorageService` (Mcp) ConcurrentDictionary stores `FileCount` but doesn't compute it (no crawler integration needed for STDIO).

### Suggested implementation path

1. Add `FileCount` to `ComponentInformation` record (default 0)
2. Add `FileCount` to `ComponentEntity` + column mapping
3. In `IndexingEngine.RunIndexingAsync`, when iterating over crawled files:
   - Build a dictionary mapping component name → file count
   - Pass file counts into `ComponentInformation` when calling `StoreComponentMappingAsync`
4. Update `HybridStorageService` upsert to persist `FileCount`

### Acceptance criteria

- `ComponentInformation` record has `FileCount` field
- `ComponentEntity` has `FileCount` stored in DB
- After indexing, each component's `FileCount` reflects the number of indexed files in that component
- File count persists across restarts (for AspNet SQLite/pgvector/sqlserver)
- Tests verify file count is stored and retrieved correctly

### Files likely involved

- `src/CodeMemory/Storage/Models.cs`
- `src/CodeMemory/Services/IndexingEngine.cs`
- `src/CodeMemory.AspNet/Registry/ComponentEntity.cs`
- `src/CodeMemory.AspNet/Registry/RepoRegistryDbContext.cs`
- `src/CodeMemory.AspNet/Storage/HybridStorageService.cs`
- `tests/CodeMemory.Tests/Storage/HybridStorageServiceTests.cs`

---

## Task 4: API Endpoints for Component CRUD

### Priority

Medium

### Goal

Add REST API endpoints so the UI can list, update, and soft-delete components per repo.

### Why this exists

The UI needs backend endpoints. These also enable programmatic component management.

### Scope

- Add three endpoints in `Program.cs` (alongside existing `/api/repos/...`):
  - `GET /api/repos/{name}/components` — list all non-deleted components for a repo
  - `PUT /api/repos/{name}/components` — update a component's Kind/Type (body: `{ buildFilePath, componentKind, componentType }`)
  - `DELETE /api/repos/{name}/components/{*buildFilePath}` — soft delete a component
- Use `RepoRegistryDbContext` directly to query/manage components
- Return JSON responses with proper status codes (200, 404, 400)
- Validate enum values on PUT

### Constraints

- Endpoints must resolve repo by name (join or lookup via `RegisteredRepos`)
- BuildFilePath in DELETE route may contain slashes — use `{*buildFilePath}` catch-all or URL-encode
- PUT should only update `ComponentKind` and `ComponentType`; other fields are managed by indexing

### Suggested implementation path

1. Add `GET /api/repos/{name}/components` — query `Components` where `RegisteredRepo.Name == name && !IsDeleted`, project to JSON
2. Add `PUT /api/repos/{name}/components` — parse body, validate enums, find existing, update Kind/Type, save
3. Add `DELETE /api/repos/{name}/components/{*buildFilePath}` — find by repo + path, set `IsDeleted = true`, `DeletedAt = UtcNow`, save
4. Test with HTTP client or integration tests

### Acceptance criteria

- `GET /api/repos/{name}/components` returns JSON array of components for a known repo
- `PUT` updates Kind/Type and returns updated component
- `DELETE` sets soft-delete and returns 200
- Invalid enum values on PUT return 400
- Unknown repo name returns 404
- Soft-deleted components do not appear in GET results

### Files likely involved

- `src/CodeMemory.AspNet/Program.cs`
- `tests/CodeMemory.Tests/Mcp/AspNetSqlQueryToolTests.cs` (for integration test patterns)

---

## Task 5: Repo Detail UI Page

### Priority

High

### Goal

Create a Razor Page at `/Repos/{name}` showing repo info and an editable, deletable components table.

### Why this exists

Users need a concrete interface to see, correct, and remove detected components. This is the primary user-facing output of Phase 4.

### Scope

- Create `Pages/Repos/Detail.cshtml` + `Pages/Repos/Detail.cshtml.cs`
- Repo info card: name, local path, git URL, clone status, index status, last indexed timestamp
- Components table with columns: BuildFilePath, ComponentName, Kind (dropdown), Type (dropdown), FileCount, Edit/Save, Delete
- Edit mode: row becomes editable with Kind/Type dropdowns, Save/Cancel buttons
- Delete: confirmation modal triggers DELETE API call
- Add a "View" link from the dashboard (`Pages/Index.cshtml`) repo table to `/Repos/{name}`
- Fetch data from `/api/repos/{name}/components` (client-side or server-side — TBD)

### Constraints

- Use existing Bootstrap 5 + Bootstrap Icons CDN (no new JS dependencies)
- No static files (no wwwroot) — keep using CDN
- Follow existing Razor Pages patterns in the project (`_Layout.cshtml`, tag helpers)
- Page must handle: repo not found (404), repo with no components (empty state message)

### Suggested implementation path

1. Create `Pages/Repos/Detail.cshtml.cs` — PageModel loads repo info from `RepoRegistryDbContext`, loads components from API
2. Create `Pages/Repos/Detail.cshtml` — repo info card + components table
3. Add edit/delete functionality with vanilla JS + Bootstrap modal
4. Add "View" link to `Pages/Index.cshtml` repo table
5. Test manually via browser

### Acceptance criteria

- `/Repos/{name}` renders with repo info card
- Components table shows all non-deleted components
- User can change Kind/Type via dropdown and save — change persists after page reload
- User can delete a component — it disappears and does not reappear after page reload
- Unknown repo shows a "not found" message
- Dashboard has a link to the detail page for each repo

### Files likely involved

- `src/CodeMemory.AspNet/Pages/Repos/Detail.cshtml` (new)
- `src/CodeMemory.AspNet/Pages/Repos/Detail.cshtml.cs` (new)
- `src/CodeMemory.AspNet/Pages/Index.cshtml`
- `src/CodeMemory.AspNet/Pages/Index.cshtml.cs`

---

## Task 6: Tests

### Priority

Medium

### Goal

Add and update tests for all Phase 4 changes, ensuring the smart upsert, soft delete, file count, and UI layer work correctly.

### Scope

- Update `HybridStorageServiceTests`:
  - Verify existing components survive re-index (user edit persistence)
  - Verify soft-deleted components are not re-added on re-index
  - Verify file count is stored and retrieved
- Update `MockServices` for renamed types
- Add API endpoint tests (integration-style via `WebApplicationFactory`) if feasible
- UI tests are manual (no Selenium/Playwright in the project)

### Constraints

- Follow existing test patterns: NUnit 4.x, AAA, no mocking libraries
- Use `TestRepoRegistryDbContextFactory` from shared test helper
- Integration tests requiring pgvector are marked `[Explicit]`

### Acceptance criteria

- All existing tests still pass
- New tests verify smart upsert logic (user edits survive re-index)
- New tests verify soft-delete round-trip
- New tests verify file count
- `MockStorageService` uses `ComponentInformation` (not the old name)

### Files likely involved

- `tests/CodeMemory.Tests/MockServices.cs`
- `tests/CodeMemory.Tests/Storage/HybridStorageServiceTests.cs`
- `tests/CodeMemory.Tests/Storage/TestRepoRegistryDbContextFactory.cs`

---

## Suggested Agent Handout Batches

### Batch A: Rename + Types (Task 1)

A single agent can handle all the find-and-replace work across ~15 files in one pass.

### Batch B: Storage Logic (Tasks 2 + 3)

One agent, sequential: soft-delete merge logic first, then file count. Both touch `HybridStorageService.cs`.

### Batch C: API + UI (Tasks 4 + 5)

One agent after Batch B. API endpoints first, then the page that consumes them.

### Batch D: Tests (Task 6)

Can start after Batch A for unit-level tests, finalize after Batch C.

## Final Checklist

- every task has a clear owner-sized scope
- every task has acceptance criteria
- decision-gate tasks are clearly marked
- likely files are listed to reduce agent search time
- execution order reflects real dependencies
- shared files that may cause merge conflicts are noted
