# LiteGraph In-Memory Storage Provider — Spike Plan

## Purpose

Spike a pure in-memory storage provider for LiteGraph (implementing `GraphRepositoryBase`) that uses no SQLite, no `:memory:` mode, no file I/O. This proves LiteGraph can be used as a zero-dependency ephemeral graph store — the LiteGraph equivalent of CodeMemory's `InMemoryVectorStore`.

## Why This Exists

Current LiteGraph's ephemeral path uses `SqliteGraphRepository(InMemory: true)` which still depends on `Microsoft.Data.Sqlite` native binaries and creates an in-process SQLite engine. A pure in-memory provider (`InMemoryGraphRepository`) removes that dependency entirely — useful for:

- **Anonymity/control**: absolutely zero disk I/O, zero native binaries, zero temp files
- **CI/testing**: no SQLite native binary requirement on test runners
- **Quick spin-up**: no database initialization overhead
- **Embedded scenarios**: no native dependencies to ship

## Relationship to PLAN.md

This spike feeds directly into **PLAN.md Task 5 (Ephemeral LiteGraph Mode)** as the preferred Path B — a true zero-dependency ephemeral backend that replaces `SqliteGraphRepository(InMemory: true)` (which still requires `Microsoft.Data.Sqlite` native binaries). If this spike succeeds, CodeMemory's `LiteGraphStorageService` can use `InMemoryGraphRepository` via the same `GraphRepositoryFactory` that already produces `SqliteGraphRepository` and `PostgresqlGraphRepository`.

## Success Criteria

- `InMemoryGraphRepository` compiles, implements all `GraphRepositoryBase` abstract methods
- All `LiteGraphClient` operations work against it (CRUD nodes, edges, labels, tags, vectors, search)
- Zero file I/O (verified)
- Zero external dependencies beyond .NET BCL
- Benchmarks demonstrate faster spin-up than `SqliteGraphRepository(InMemory: true)`

---

## Suggested Execution Order

1. Task 1: Scope LiteGraph's `GraphRepositoryBase` surface area
2. Task 2: `InMemoryGraphRepository` — data model + core storage
3. Task 3: CRUD methods (Tenant, Graph, Node, Edge, User, Credential)
4. Task 4: Label, Tag, and Vector methods
5. Task 5: Vector index (in-memory HNSW or brute-force)
6. Task 6: Integration verification with LiteGraphClient
7. Task 7: Benchmarks vs `SqliteGraphRepository(InMemory: true)`

## Coordination Notes

- Tasks 2–5 should be treated as one spike — the value is the whole thing working end-to-end
- Task 6 proves correctness, Task 7 proves the thesis
- The output of this spike (the `InMemoryGraphRepository` class) is the deliverable — not integration into CodeMemory

---

## Task 1: Scope LiteGraph's GraphRepositoryBase Surface

### Priority

High

### Goal

Map every abstract method on `GraphRepositoryBase` that `InMemoryGraphRepository` must implement — so the spike has a complete inventory before coding.

### Why this exists

Without a full method inventory, the spike will miss methods and waste context switches.

### Scope

- Read `GraphRepositoryBase` source to enumerate all abstract/override methods
- Identify which methods have complex business logic vs simple CRUD
- Identify which methods interact with `VectorIndexManager` and `IVectorIndex`
- Map all interfaces used (`IGraphMethods`, `INodeMethods`, `IEdgeMethods`, `IVectorMethods`, `ILabelMethods`, `ITagMethods`, `IUserMethods`, `ICredentialMethods`, `IAdminMethods`, etc.)

### Acceptance criteria

- Complete inventory of every abstract method on `GraphRepositoryBase`
- Count of methods by category: CRUD simple / CRUD complex / index-related / admin
- List of sub-interfaces exposed as properties on `GraphRepositoryBase`

### Files likely involved

- `E:\github\litegraph\src\LiteGraph\GraphRepositories\GraphRepositoryBase.cs`
- `E:\github\litegraph\src\LiteGraph\GraphRepositories\Sqlite\SqliteGraphRepository.cs` (reference)
- `E:\github\litegraph\src\LiteGraph\GraphRepositories\Postgresql\PostgresqlGraphRepository.cs` (reference)

---

## Task 2: InMemoryGraphRepository — Data Model + Core Storage

### Priority

High

### Goal

Implement the data model and core storage infrastructure for `InMemoryGraphRepository` — the concurrent dictionaries and locking strategies that back all operations.

### Why this exists

Without the core data model, no CRUD methods can be implemented.

### Scope

- Design the in-memory backing stores:
  - `ConcurrentDictionary<Guid, Tenant>` for tenants
  - `ConcurrentDictionary<Guid, Graph>` for graphs
  - `ConcurrentDictionary<Guid, Node>` for nodes
  - `ConcurrentDictionary<Guid, Edge>` for edges
  - `ConcurrentDictionary<Guid, UserMaster>` for users
  - `ConcurrentDictionary<Guid, Credential>` for credentials
  - `Dictionary<...>` for labels/tags lookups
  - `List<VectorMetadata>` for vectors with concurrent access
- Implement `InitializeRepository()` — no-op
- Implement `Dispose()`, `Flush()` — no-ops
- Implement thread-safe read/write patterns (lock per entity type or global)
- Stub all property accessors (`Graph`, `Node`, `Edge`, `Vector`, `Label`, `Tag`, `User`, `Credential`, `Admin`, `Logging`, `Settings`)
- Stub all sub-method-group classes (`InMemoryGraphMethods`, `InMemoryNodeMethods`, etc.)

### Constraints

- Zero file I/O. No temp files, no serialization to disk.
- Thread-safe: concurrent reads and writes must not corrupt state.
- GUID generation follows LiteGraph conventions (`Guid.NewGuid()` with format "D").

### Suggested implementation path

1. Create `InMemoryGraphRepository : GraphRepositoryBase`
2. Constructor takes name parameter only (no `DatabaseSettings`)
3. Create `ConcurrentDictionary` fields for each entity type
4. Create sub-method group classes as nested or companion classes
5. Implement `InitializeRepository()` as `Task.CompletedTask`

### Acceptance criteria

- `InMemoryGraphRepository` compiles with all abstract member stubs
- `InitializeRepository()`, `Dispose()`, `Flush()` complete without exceptions
- All property getters return non-null stub instances

### Files likely involved

- `src/LiteGraph/GraphRepositories/InMemory/InMemoryGraphRepository.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryGraphMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryNodeMethods.cs` (new)
- ... (additional method group files)

---

## Task 3: CRUD Methods — Tenant, Graph, Node, Edge, User, Credential

### Priority

High

### Goal

Implement the core CRUD method groups for tenants, graphs, nodes, edges, users, and credentials against the in-memory backing stores.

### Why this exists

These are the primary data types that CodeMemory's integration needs — nodes (symbols) and edges (relationships).

### Scope

- `InMemoryTenantMethods` — Create, Read, Update, Delete, Exists, List tenants
- `InMemoryGraphMethods` — Create, Read, Update, Delete, Exists, List graphs within tenant
- `InMemoryNodeMethods` — Create, Read (by GUID / by multiple GUIDS), Update, Delete, Exists, List by tenant/graph, parent/child traversal, neighbor/edge traversal, route finding
- `InMemoryEdgeMethods` — Create, Read, Update, Delete, Exists, List by tenant/graph, by source/target node, by edge type
- `InMemoryUserMethods` — Create, Read, Update, Delete, List, Authenticate
- `InMemoryCredentialMethods` — Create, Read, Update, Delete, List, Authenticate

### Constraints

- Must match `SqliteGraphRepository` behavior exactly for these operations (same validation, same return types, same exception types)
- Node GUID uniqueness enforced across the graph
- Edge source/target node GUID validation (referential integrity)
- Tenant isolation: operations scoped to tenant

### Suggested implementation path

1. Implement each method group class one at a time
2. Use `SqliteGraphRepository`'s implementation as the reference — same logic, replace SQL with `ConcurrentDictionary` operations
3. Add `lock` per entity type for compound operations

### Acceptance criteria

- Full CRUD round-trips for tenants, graphs, nodes, edges, users, credentials
- Node parent/child traversal returns correct descendants
- Edge source/target traversal returns connected nodes
- Duplicate create returns appropriate error (or upsert)
- Missing entity returns null (not exception)

### Files likely involved

- `src/LiteGraph/GraphRepositories/InMemory/InMemoryTenantMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryGraphMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryNodeMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryEdgeMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryUserMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryCredentialMethods.cs` (new)

---

## Task 4: Label, Tag, and Vector Methods

### Priority

High

### Goal

Implement label, tag, and vector metadata operations against in-memory backing stores.

### Why this exists

Labels and tags are how CodeMemory will query symbols by kind and file path. Vectors are required for semantic search.

### Scope

- `InMemoryLabelMethods` — Add, Remove, List labels on graphs/nodes/edges; search by label
- `InMemoryTagMethods` — Add, Remove, List tags on graphs/nodes/edges; search by tag
- `InMemoryVectorMethods` — Create (store vector + metadata), Read, Update, Delete vector metadata; list vectors by node/graph/tenant; search vectors (fall back to brute-force if no HNSW index)

### Constraints

- Labels and tags stored as hash sets or dictionaries per entity
- Vector search must produce same results order as `SqliteGraphRepository`'s brute-force fallback (cosine similarity, Euclidean distance, dot product)
- Thread safety for concurrent label/tag/vector mutations

### Suggested implementation path

1. Store labels as `ConcurrentDictionary<Guid, HashSet<string>>` keyed by entity GUID
2. Store tags as `ConcurrentDictionary<Guid, Dictionary<string, string>>` keyed by entity GUID
3. Store vectors as `ConcurrentDictionary<Guid, VectorMetadata>` keyed by vector GUID
4. Implement brute-force vector search: iterate all vectors, compute similarity, sort, return top-K

### Acceptance criteria

- Labels can be added/removed/queried on nodes, edges, and graphs
- Tags can be added/removed/queried with key-value pairs
- Vector CRUD operations work correctly
- Vector search returns correct top-K results for cosine similarity
- Vector search matches `SqliteGraphRepository` results (same vectors, same query → same results)

### Files likely involved

- `src/LiteGraph/GraphRepositories/InMemory/InMemoryLabelMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryTagMethods.cs` (new)
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryVectorMethods.cs` (new)

---

## Task 5: Vector Index — In-Memory HNSW or Brute-Force

### Priority

Medium

### Goal

Decide whether to implement a lightweight in-memory HNSW index or rely on brute-force vector search for the spike, then implement the chosen approach.

### Why this exists

Vector search performance is critical for semantic search. The spike should demonstrate whether a full HNSW implementation is needed or brute-force is acceptable for CodeMemory's scale (thousands-millions of chunks).

### Decision required

- Is brute-force vector search (linear scan over all vectors) fast enough for CodeMemory's anticipated scale?
- If yes: implement brute-force in `InMemoryVectorMethods`
- If no: implement a stripped-down in-memory HNSW (`InMemoryHnswIndex : IVectorIndex`)

### Scope

- Implement `InMemoryVectorMethods.SearchAsync` with brute-force scan
- Benchmark brute-force at 1K, 10K, 100K, 1M vectors at 1536 dimensions
- If brute-force is insufficient: implement `InMemoryVectorIndex : IVectorIndex` with:
  - `AddAsync` / `UpdateAsync` / `RemoveAsync`
  - `SearchAsync` with configurable ef
  - `SaveAsync` / `LoadAsync` as no-ops
- Wire `VectorIndexManager` to use `InMemoryVectorIndex`

### Constraints

- If HNSW is implemented, it must preserve LiteGraph's `IVectorIndex` contract
- Must handle the same search types: CosineSimilarity, EuclideanDistance, DotProduct

### Suggested implementation path

1. Start with brute-force in `InMemoryVectorMethods.SearchAsync`
2. Run benchmarks with `dotnet run -c Release -- --benchmark`
3. If >100ms for 10K vectors, implement `InMemoryVectorIndex : IVectorIndex`
4. Wire into `VectorIndexManager` by overriding `GetOrCreateIndexAsync` or providing a custom factory

### Acceptance criteria

- Vector search returns correct results (verified against known query with known data)
- Benchmark results documented in the spike write-up
- Decision clear: brute-force sufficient or HNSW needed

### Files likely involved

- `src/LiteGraph/GraphRepositories/InMemory/InMemoryVectorMethods.cs`
- `src/LiteGraph/GraphRepositories/InMemory/InMemoryVectorIndex.cs` (new, if needed)
- `benchmarks/InMemoryVectorBenchmark.cs` (new, if needed)

---

## Task 6: Integration Verification with LiteGraphClient

### Priority

High

### Goal

Verify that `LiteGraphClient` works end-to-end when backed by `InMemoryGraphRepository` — all operations flow through the same client API without a real database.

### Why this exists

The spike's value is proven only when a real `LiteGraphClient` functions correctly against the in-memory provider.

### Scope

- Instantiate `LiteGraphClient` with `InMemoryGraphRepository` (through factory or direct wiring)
- Write integration tests that exercise:
  - Tenant CRUD
  - Graph CRUD within tenant
  - Node CRUD with labels and tags
  - Edge CRUD between nodes
  - Vector metadata CRUD + vector search
  - Label/tag queries
  - Graph traversal (node parent/child, edge source/target)
  - Tenant isolation (data not visible across tenants)
- Verify all results match `SqliteGraphRepository(InMemory: true)` baseline

### Constraints

- Must use the public `LiteGraphClient` API — not internal repository methods directly
- Same test suite should run against both `InMemoryGraphRepository` and `SqliteGraphRepository(InMemory: true)` for comparison

### Suggested implementation path

1. Add `GraphRepositoryFactory` support for the new provider (or construct directly)
2. Write parameterized tests that swap the repository implementation
3. Run all existing LiteGraph integration tests against the in-memory provider

### Acceptance criteria

- All integration tests pass against `InMemoryGraphRepository`
- `LiteGraphClient` operations produce identical results with both providers
- No file I/O detected during test execution (verify with `File` watcher concept or strace)

### Files likely involved

- `tests/LiteGraph.Tests/InMemory/InMemoryGraphRepositoryTests.cs` (new)
- `src/LiteGraph/GraphRepositories/GraphRepositoryFactory.cs` (add `InMemory` case)

---

## Task 7: Benchmarks vs SqliteGraphRepository(InMemory: true)

### Priority

Medium

### Goal

Benchmark `InMemoryGraphRepository` against `SqliteGraphRepository(InMemory: true)` to quantify the performance thesis — no SQLite overhead, no native binary marshalling.

### Why this exists

Proves (or disproves) the performance advantage of a pure in-memory provider.

### Scope

- Benchmark scenarios:
  - Provider initialization time (cold start)
  - Node creation (1K, 10K batch)
  - Node read by GUID (1K random lookups)
  - Node list by graph (100K node graph)
  - Edge traversal (10K edges, random walk)
  - Vector search brute-force (1K, 10K, 100K at 1536 dims)
- Measure: wall-clock time, memory allocation, GC pressure

### Constraints

- Benchmarks run on the same hardware, same process
- Both providers tested with identical data and operations
- Results reported in the spike write-up as a decision record

### Suggested implementation path

1. Use `BenchmarkDotNet` for rigorous microbenchmarks
2. Add a benchmark project or use `dotnet run` with `Stopwatch`
3. Run at least 5 iterations per scenario
4. Write up results in the spike conclusion

### Acceptance criteria

- Cold start: `InMemoryGraphRepository` is measurably faster than `SqliteGraphRepository(InMemory: true)`
- CRUD operations: at parity or better
- Vector search at 10K vectors: brute-force vs SQLite-backed comparison
- Benchmark results published in the spike summary

### Files likely involved

- `benchmarks/LiteGraph.Benchmarks/InMemoryVsSqliteBenchmarks.cs` (new)
- `benchmarks/LiteGraph.Benchmarks/LiteGraph.Benchmarks.csproj` (new)

---

## Suggested Agent Handout Batches

### Batch A: Discovery (1 agent)

- **Task 1** — method inventory scoping

### Batch B: Core Implementation (1 agent, after Batch A)

- **Tasks 2 + 3** — data model + CRUD (these are tightly coupled, one agent)

### Batch C: Remaining Methods + Verification (1 agent, after Batch B)

- **Tasks 4 + 5 + 6** — labels, tags, vectors, integration tests

### Batch D: Performance (1 agent, after Batch C)

- **Task 7** — benchmarks + write-up

---

## Risk Register

| Risk | Mitigation |
|---|---|
| `GraphRepositoryBase` has 100+ abstract methods | Task 1 provides exact count; batch implementation by method group |
| Brute-force vector search too slow at scale | Task 5 decides HNSW vs brute-force based on real benchmarks |
| LiteGraph's `VectorIndexManager` tightly coupled to file I/O | Override `GetOrCreateIndexAsync` with in-memory index factory |
| Concurrent access races in `ConcurrentDictionary` | Use per-entity-type locking for compound read-write operations |
| In-memory provider misses subtle SQLite behavior (casing, defaults) | Integration test suite from Task 6 catches mismatches |
