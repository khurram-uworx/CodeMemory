# LiteGraph Storage Backend — Integration Plan

## Vision

Replace CodeMemory's flat `VectorStore`-backed storage with **LiteGraph as an in-process property graph backend**. LiteGraph adds: native graph query language, HNSW vector indexing, ACID transactions, labels/tags, and multi-tenant persistence — all without a separate process.

> **Important**: LiteGraph does NOT use `Microsoft.Extensions.VectorData`. It has its own `GraphRepositoryBase` abstraction with raw SQL providers (SQLite, PostgreSQL) and a custom `IVectorIndex` interface for HNSW. CodeMemory's `IStorageService` is the sole abstraction boundary — `LiteGraphStorageService` implements it against `LiteGraphClient` directly, not through `VectorStore`. See `LITEGRAPH-STORAGE.md` for the full comparison.

## Architecture

```
CodeMemory Indexing Pipeline (unchanged)
  └─ Symbols, Relationships, Chunks
       └─ IStorageService (abstraction — unchanged)
            ├─ InMemoryVectorStore (existing — "Provider": "inmemory")
            ├─ SqliteVectorStore   (existing — "Provider": "sqlite")
            └─ LiteGraphStorageService (new — "Provider": "litegraph")
                 └─ LiteGraphClient (in-process library, no server)
                      ├─ Graph (per repo)
                      │    ├─ Nodes      ← Symbols
                      │    ├─ Edges      ← Relationships
                      │    └─ Vectors    ← Chunk embeddings, HNSW-indexed
                      └─ Graph Query DSL ← new MCP tool surface
```

## Data Model Mapping

| CodeMemory Concept | LiteGraph Mapping |
|---|---|
| Repo | `Graph` within a `Tenant` |
| `SymbolRecord` | `Node` with labels = Kind, tags = modifiers, data = full record |
| `RelationshipRecord` | `Edge` between symbol nodes, label = relationship type |
| `ChunkRecord.Content` | `Node` data field or attached to symbol node |
| `ChunkRecord.Embedding` | `VectorMetadata` on chunk node |
| Semantic search | `VectorSearchRequest` with HNSW index |

## Execution Order

1. **Task 1**: LiteGraph dependency & LiteGraphClient service skeleton (decision gate)
2. **Task 2**: Read-path implementation (symbols, relationships, chunks, search)
3. **Task 3**: Write-path implementation (store symbols, relationships, chunks + clear)
4. **Task 4**: Config integration in `Program.cs` + runtime switching
5. **Task 5**: Ephemeral LiteGraph mode (in-memory SQLite / anonymous tenant)
6. **Task 6**: Tests
7. **Task 7**: Expose LiteGraph DSL as new MCP tool (stretch)

---

## Task 1: LiteGraph Dependency & Service Skeleton

### Priority

High

### Goal

Add LiteGraph as a project dependency, define the `LiteGraphStorageService` class implementing `IStorageService`, and validate the in-process `LiteGraphClient` spins up without a server.

### Why this exists

First decision gate — proves LiteGraph works as an in-process library without process or network overhead.

### Decision required

- How to reference LiteGraph: NuGet package, submodule, or direct project reference?
- Tenant naming convention: one tenant per repo, or one tenant for all repos?
- Graph naming convention per repo

### Scope

- Add LiteGraph project/NuGet reference to `CodeMemory.Storage`
- Create `LiteGraphStorageService : IStorageService` skeleton (all methods throw `NotSupportedException` or return default)
- Create `LiteGraphStorageOptions` record (TenantGUID, GraphGUID, DbSettings)
- Initialize `LiteGraphClient` in constructor with ephemeral SQLite in-memory mode
- Verify in-process startup with a smoke test

### Constraints

- No separate LiteGraph server process — must use `LiteGraphClient` directly
- LiteGraph MUST operate in an ephemeral mode to support the "when MCP dies everything dies" contract. **Initial default**: `SqliteGraphRepository(InMemory: true)` — this works today and requires no extra implementation. **Future upgrade**: if the `InMemoryGraphRepository` spike (`LITEGRAPH-STORAGE-SPIKE.md`) succeeds, it becomes the preferred ephemeral backend (see Task 5).
- Must not break existing `"inmemory"` or `"sqlite"` providers

### Suggested implementation path

1. Add `<ProjectReference>` to LiteGraph's main project or add NuGet package
2. Create `CodeMemory.Storage/LiteGraph/LiteGraphStorageService.cs`
3. In constructor: `new LiteGraphClient()` with `DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, InMemory = true }` — ephemeral SQLite in-memory mode (no file path needed)
4. Tenant setup: create tenant + graph in `InitializeAsync`
5. All IStorageService methods throw `NotSupportedException` initially

### Acceptance criteria

- `LiteGraphStorageService` compiles and implements all 15 methods of `IStorageService`
- `LiteGraphClient` initialized in-process with no network calls
- Existing tests for `"inmemory"` and `"sqlite"` providers still pass
- A manual test proves: create `LiteGraphStorageService`, call `InitializeAsync`, no exceptions

### Files likely involved

- `src/CodeMemory.Storage/CodeMemory.Storage.csproj`
- `src/CodeMemory.Storage/StorageService.cs` (reference for pattern)
- `src/CodeMemory.Storage/LiteGraph/LiteGraphStorageService.cs` (new)
- `src/CodeMemory.Storage/LiteGraph/LiteGraphStorageOptions.cs` (new)

---

## Task 2: Read-Path Implementation

### Priority

High

### Goal

Implement all read operations on `LiteGraphStorageService` — querying symbols, relationships, chunks, and semantic search from LiteGraph.

### Why this exists

The read path is the most critical for MCP tools. Without reads, no tool returns data.

### Scope

- `GetSymbolAsync` — lookup a node by name → `SymbolRecord`
- `GetSymbolsByFileAsync` — list nodes by file path tag/filter
- `GetSymbolsByKindAsync` — list nodes by label (Kind)
- `GetRelationshipAsync` — lookup an edge → `RelationshipRecord`
- `GetRelationshipsBySourceAsync` — outgoing edges from a node
- `GetRelationshipsByTargetAsync` — incoming edges to a node
- `GetChunkAsync` — lookup chunk node → `ChunkRecord`
- `GetChunksBySymbolAsync` — list chunk nodes attached to a symbol
- `SearchChunksAsync` — vector similarity search via LiteGraph's `VectorSearchRequest` / HNSW index
- `RepoRoot` property

### Constraints

- Must handle LiteGraph's async query patterns correctly (no sync-over-async)
- Must respect cancellation tokens
- Must preserve LiteGraph's multi-tenant structure

### Suggested implementation path

1. Write a helper that maps LiteGraph `Node` → `SymbolRecord`
2. Write a helper that maps LiteGraph `Edge` → `RelationshipRecord`
3. Write a helper that maps LiteGraph vector search results → `ScoredChunk`
4. HNSW index creation for chunks in `InitializeAsync`
5. Implement each read method using `LiteGraphClient.{Nodes,Edges,Vector}.{Get,Search,...}`

### Acceptance criteria

- All 9 read methods return correct data from LiteGraph
- `SearchChunksAsync` uses HNSW index for approximate nearest neighbor (not brute-force)
- Empty store returns empty lists/null (not exceptions)

### Files likely involved

- `src/CodeMemory.Storage/LiteGraph/LiteGraphStorageService.cs`
- `src/CodeMemory.Storage/LiteGraph/MappingHelpers.cs` (new)

---

## Task 3: Write-Path Implementation

### Priority

High

### Goal

Implement all write operations — storing symbols, relationships, chunks, and clearing the store.

### Why this exists

Without writes, nothing makes it into LiteGraph.

### Scope

- `InitializeAsync` — create tenant, create graph, ensure collections, create HNSW index config
- `StoreSymbolsAsync` — upsert nodes with labels + data
- `StoreChunksAsync` — upsert chunk nodes with vector metadata + HNSW index updates
- `StoreRelationshipsAsync` — upsert directed edges
- `ClearAllAsync` — delete graph and re-create (ephemeral full reset)

### Constraints

- Batched writes (respect LiteGraph batch limits)
- HNSW index must be updated when chunks are stored (not rebuilt from scratch each time)
- ClearAllAsync must truly wipe everything (matches ephemeral contract)

### Suggested implementation path

1. `StoreSymbolsAsync` → `LiteGraphClient.Nodes.CreateOrUpdateAsync` with labels
2. `StoreRelationshipsAsync` → `LiteGraphClient.Edges.CreateOrUpdateAsync`
3. `StoreChunksAsync` → create node per chunk + attach `VectorMetadata` via `LiteGraphClient.Vector.SetVectorAsync`
4. `ClearAllAsync` → delete graph node (cascades to all nodes/edges/vectors)

### Acceptance criteria

- Write then read round-trips correctly for symbols, relationships, chunks
- Multiple writes to the same symbol are idempotent (upsert)
- `ClearAllAsync` returns store to a clean initial state
- Batch writes of 1000+ symbols complete without timeout

### Files likely involved

- `src/CodeMemory.Storage/LiteGraph/LiteGraphStorageService.cs`
- `src/CodeMemory.Storage/LiteGraph/MappingHelpers.cs`

---

## Task 4: Config Integration in Program.cs

### Priority

High

### Goal

Wire `"Provider": "litegraph"` into the existing storage provider selection in `CodeMemory.AspNet/Program.cs` and `CodeMemory.Mcp/Program.cs`, making it a first-class option alongside `"inmemory"` and `"sqlite"`.

### Why this exists

Without config wiring, the provider can't be selected at deployment time.

### Scope

- Add `"litegraph"` to the provider switch in `Program.cs` (both AspNet and Mcp hosts)
- Support `Storage:LiteGraph` config section for LiteGraph-specific settings
- Expose LiteGraph mode in root `GET /` response
- Dev config: add a litegraph profile to `appsettings.Development.json`

### Constraints

- Must not break existing `"inmemory"` and `"sqlite"` config paths
- Must handle the case where LiteGraph reference is missing (graceful degradation)

### Suggested implementation path

1. In `Program.cs`, add `"litegraph"` branch alongside existing `if (useSqlite)` / `else`
2. Extract LiteGraph config from `builder.Configuration.GetSection("Storage:LiteGraph")`
3. Construct `LiteGraphClient` with appropriate `DatabaseSettings` from config
4. Register via `storageRegistry.Register(name, new LiteGraphStorageService(...))`
5. Add `appsettings.LiteGraph.json` for easy switching

### Acceptance criteria

- Setting `"Storage":"Provider": "litegraph"` in config starts CodeMemory with LiteGraph backend
- Root `GET /` shows `"storageProvider": "litegraph"` and related LiteGraph info
- Existing inmemory/sqlite configs unchanged and still work

### Files likely involved

- `src/CodeMemory.AspNet/Program.cs`
- `src/CodeMemory.AspNet/appsettings.json`
- `src/CodeMemory.AspNet/appsettings.Development.json`
- `src/CodeMemory.AspNet/Configuration/ServiceRegistry.cs` (if new service types needed)
- `src/CodeMemory.Mcp/Program.cs`

---

## Task 5: Ephemeral LiteGraph Mode

### Priority

Medium

### Goal

Ensure LiteGraph can run in a fully ephemeral mode — no disk writes, no persistence, everything lost when the process exits. This preserves the "anonymity/control" contract that in-memory storage provides.

### Why this exists

The in-memory provider's key selling point is "when MCP dies everything dies." LiteGraph must offer the same guarantee. Two paths exist: SQLite `:memory:` (still depends on `Microsoft.Data.Sqlite` native binaries) and a pure in-memory `InMemoryGraphRepository` (zero native deps).

### Decision required

- **Path A**: `SqliteGraphRepository(InMemory: true)` — works today, but still requires `Microsoft.Data.Sqlite` native binaries
- **Path B**: `InMemoryGraphRepository` from the spike (see `LITEGRAPH-STORAGE-SPIKE.md`) — zero native dependencies, but needs implementation first

### Scope

- Add `LiteGraphStorageOptions.Ephemeral` flag (default: true)
- When ephemeral + Path A: use `SqliteGraphRepository(InMemory: true)`, no on-disk HNSW files
- When ephemeral + Path B (spike succeeds): use `InMemoryGraphRepository` — zero native deps, zero I/O
- When non-ephemeral: use file-backed `SqliteGraphRepository` + persisted HNSW index
- Test: create store, write data, dispose, recreate, verify emptiness

### Constraints

- Ephemeral mode must produce zero disk writes (verify with file system watcher or procmon concept)
- Non-ephemeral mode must persist across restarts

### Suggested implementation path

1. **Default ephemeral**: `SqliteGraphRepository(_, InMemory: true)` — works immediately
2. **Check spike output**: if `InMemoryGraphRepository` from `LITEGRAPH-STORAGE-SPIKE.md` is complete and stable, swap to it for true zero-dependency ephemeral mode
3. Address HNSW: `SqliteGraphRepository` persists `.hnsw` files via `VectorIndexManager`. In ephemeral mode, wrap with `VolatileVectorIndex` that skips `SaveAsync`/`LoadAsync`, or use `InMemoryGraphRepository`'s brute-force vector scan
4. Gate with `LiteGraphStorageOptions.Ephemeral` flag

### Acceptance criteria

- With `Ephemeral: true`, no files written to disk during indexing or querying
- After process restart, all data is gone
- With `Ephemeral: false`, data persists across restarts in SQLite file + HNSW files
- Config option documented

### Files likely involved

- `src/CodeMemory.Storage/LiteGraph/LiteGraphStorageOptions.cs`
- `src/CodeMemory.Storage/LiteGraph/LiteGraphStorageService.cs`
- `src/CodeMemory.Storage/LiteGraph/VolatileVectorIndex.cs` (new, if needed)

---

## Task 6: Tests

### Priority

High

### Goal

Full test coverage for `LiteGraphStorageService` matching the existing `StorageServiceTests` pattern.

### Why this exists

Without tests, the integration will regress.

### Scope

- Mirror `StorageServiceTests` structure for LiteGraph
- Test each read/write method
- Test round-trip: store symbols → query by kind → verify
- Test round-trip: store chunks → semantic search → verify scores
- Test round-trip: store relationships → trace upstream/downstream
- Test `ClearAllAsync` (full reset)
- Test ephemeral mode (data gone after dispose) — both Path A (SQLite `:memory:`) and Path B (`InMemoryGraphRepository` if spike succeeded)
- Test non-ephemeral mode (data persists)
- Test empty store behavior
- Test cancellation token propagation
- Test batch operations (200+ symbols, 1000+ relationships)

### Constraints

- All tests must use in-process LiteGraph — no external server
- Must run in CI without special infrastructure
- If `InMemoryGraphRepository` exists, test against both providers for parity

### Suggested implementation path

1. Copy `CodeMemory.Tests/Storage/StorageServiceTests.cs` as template
2. Replace `InMemoryVectorStore` setup with `LiteGraphClient` setup
3. Parameterize tests to run against both `SqliteGraphRepository(InMemory: true)` and `InMemoryGraphRepository` (if available)
4. Add ephemeral-specific test cases

### Acceptance criteria

- All tests pass in CI
- Test coverage matches or exceeds existing `StorageServiceTests`
- Tests complete in under 30 seconds

### Files likely involved

- `src/CodeMemory.Tests/Storage/LiteGraphStorageServiceTests.cs` (new)
- `src/CodeMemory.Tests/CodeMemory.Tests.csproj` (add LiteGraph reference)

---

## Task 7: Expose LiteGraph DSL as MCP Tool (Stretch)

### Priority

Low

### Goal

Add a new MCP tool `graph_query` that accepts LiteGraph's native graph query language and returns structured results, giving agents the full power of the graph DSL.

### Why this exists

LiteGraph's query language (MATCH, WHERE, RETURN, path traversal, vector search) is the crown jewel. Exposing it as an MCP tool lets agents ask complex structural questions that the fixed MCP tools can't express.

### Scope

- Add `LiteGraphQueryService : ILiteGraphQueryService` in `CodeMemory.Mcp.Services`
- Add `graph_query` MCP tool accepting query string + parameters
- Route query to correct LiteGraph graph per repo context
- Return structured JSON (nodes, edges, vectors)
- Add query validation + error handling
- Add default query timeout

### Constraints

- Must respect `IRepoContextAccessor` for repo routing
- Must sanitize/validate queries (no injection into LiteGraph)
- Must not expose administrative LiteGraph operations (tenant creation, etc.)

### Suggested implementation path

1. Create `CodeMemory.Mcp.Services.LiteGraphQueryService`
2. Create `CodeMemory.Mcp.Tools.GraphQueryTool`
3. Register in DI and MCP tool discovery
4. Use `LiteGraphClient.Query.ExecuteAsync` under the hood

### Acceptance criteria

- `graph_query` tool accepts a LiteGraph query string and returns results
- Repo context is correctly routed
- Malformed queries return descriptive errors (not LiteGraph internals)
- Agent can run `MATCH (n:Class) WHERE n.Name CONTAINS "Auth" RETURN n`

### Files likely involved

- `src/CodeMemory/Mcp/Services/ILiteGraphQueryService.cs` (new)
- `src/CodeMemory/Mcp/Services/LiteGraphQueryService.cs` (new)
- `src/CodeMemory/Mcp/Tools/GraphQueryTool.cs` (new)
- `src/CodeMemory/Mcp/McpTools.cs` (register tool)
- `src/CodeMemory.AspNet/Program.cs` (register service)

---

## Suggested Agent Handout Batches

### Batch A: Foundation (2 agents)

- **Agent 1**: Task 1 — dependency setup + service skeleton
- **Agent 2**: Task 4 — config integration in parallel (no runtime deps on Tasks 2-3)

### Batch B: Core Logic (2 agents, after Batch A)

- **Agent 1**: Task 2 — read path
- **Agent 2**: Task 3 — write path

### Batch C: Hardening (1 agent, after Batch B)

- **Agent 1**: Tasks 5 + 6 — ephemeral mode + tests

### Batch D: Stretch (1 agent, after Batch C)

- **Agent 1**: Task 7 — graph_query MCP tool

---

## Risk Register

| Risk | Mitigation |
|---|---|
| LiteGraph `VectorIndexManager` writes `.hnsw` files even in ephemeral mode | Wrap with `VolatileVectorIndex` that skips disk I/O |
| LiteGraphClient API doesn't expose batch upsert | Use loop with per-item calls + measure perf; escalate to LiteGraph upstream |
| LiteGraph query DSL parser has different semantics than expected | Add integration test suite exercising query tool against known data |
| LiteGraph adds new dependencies to CodeMemory | Add as conditional dependency (`<PackageReference Include="..." />` not always included) |
| LiteGraph in-memory SQLite mode doesn't support concurrent access | LiteGraphClient is designed for single-process use; route all calls through one client per graph |
