# LiteGraph Storage Architecture — Analysis

## Why This Document Exists

To capture how LiteGraph approaches storage backends vs CodeMemory's approach, so we can make an informed decision later about CodeMemory's enterprise storage layer (PostgreSQL, SQL Server, etc.) without reverse-engineering LiteGraph again.

### Key session decisions reflected here

- LiteGraph runs **in-process as a library** (`LiteGraphClient`) — no separate server, no network hop
- LiteGraph does **NOT** use `Microsoft.Extensions.VectorData` — it has its own `GraphRepositoryBase` abstraction with raw SQL providers
- The "when MCP dies everything dies" contract for anonymity/control is satisfied by either `SqliteGraphRepository(InMemory: true)` or the proposed `InMemoryGraphRepository` (see `LITEGRAPH-STORAGE-SPIKE.md`)
- CodeMemory's sole abstraction boundary is `IStorageService` — `LiteGraphStorageService` implements it directly against `LiteGraphClient`, not through `VectorStore`

---

## LiteGraph's Storage Abstraction

### Backends

| Backend | Status | Notes |
|---|---|---|---|
| **SQLite** | Full implementation | Supports `InMemory: true` for ephemeral mode |
| **PostgreSQL** | Full implementation via Npgsql | Production-ready |
| **MySQL** | Placeholder (`UnsupportedGraphRepository`) | Throws on every method |
| **SQL Server** | Placeholder (`UnsupportedGraphRepository`) | Throws on every method |
| **InMemoryGraphRepository** | Proposed (see `LITEGRAPH-STORAGE-SPIKE.md`) | Pure in-memory, zero native deps, zero I/O |

### Abstraction Stack

```
LiteGraphClient (top-level API consumer)
  └─ GraphRepositoryBase (abstract class, 15+ method groups)
       ├─ SqliteGraphRepository
       │    ├─ AdminMethods, CredentialMethods, EdgeMethods
       │    ├─ GraphMethods, LabelMethods, NodeMethods
       │    ├─ TagMethods, UserMethods, VectorMethods
       │    ├─ VectorMethodsIndexExtensions (HNSW index integration)
       │    └─ raw SQL via Microsoft.Data.Sqlite
       └─ PostgresqlGraphRepository
            ├─ same method groups
            └─ raw SQL via Npgsql

VectorIndexManager (per-graph HNSW index lifecycle)
  └─ HnswLiteVectorIndex : IVectorIndex
       ├─ AddAsync / UpdateAsync / RemoveAsync
       ├─ SearchAsync (ANN, configurable ef)
       ├─ SaveAsync / LoadAsync (.hnsw files on disk)
       └─ AddBatchAsync / RemoveBatchAsync

DatabaseSettings
  ├─ Type (Sqlite | Postgresql | Mysql | SqlServer)
  ├─ Filename (SQLite only)
  ├─ InMemory (SQLite only)
  ├─ Hostname / Port / DatabaseName / Username / Password
  ├─ Schema
  ├─ ConnectionString (override)
  ├─ MaxConnections
  └─ CommandTimeoutSeconds
```

### Key Observations

1. **No ORM, no `Microsoft.Extensions.VectorData`** — LiteGraph writes raw SQL for each backend. PostgreSQL and SQLite have separate query constants files (`VectorQueries`, `NodeQueries`, etc.) with provider-specific SQL.

2. **MySQL and SQL Server are unimplemented** — They exist in the factory and the type enum, but the repository classes extend `UnsupportedGraphRepository` which throws `NotSupportedException` on every method. The schema and query work was never done.

3. **`IVectorIndex` is a custom interface**, not `Microsoft.Extensions.VectorData`'s `VectorStore`. The HNSW implementation (`HnswLiteVectorIndex`) manages its own on-disk index files (`.hnsw`) separate from the SQL tables that store `VectorMetadata` rows.

4. **Vector storage is dual**: `VectorMetadata` rows live in the SQL database, while the HNSW approximate index lives in `.hnsw` files managed by `VectorIndexManager`. Searches go through `VectorMethodsIndexExtensions.SearchWithIndexAsync` which checks the HNSW index first, falls back to brute-force SQL scan if the index is dirty or unavailable.

5. **Provider selection is a factory pattern**: `GraphRepositoryFactory.Create(DatabaseSettings)` uses a simple `switch` — no DI, no service registry, no middleware.

---

## CodeMemory's Storage Abstraction

### Backends

| Backend | Status | Notes |
|---|---|---|
| **InMemoryVectorStore** | Default | Memori NuGet, data lost on restart |
| **SqliteVectorStore** | Optional | SK SqliteVec connector, `.memorycode/sqlvec.db` |

### Abstraction Stack

```
IStorageService (interface, 15 methods)
  └─ StorageService
       └─ wraps VectorStore (Microsoft.Extensions.VectorData)
            ├─ VectorStoreCollection<string, SymbolRecord>
            ├─ VectorStoreCollection<string, ChunkRecord>
            └─ VectorStoreCollection<string, RelationshipRecord>

StorageServiceRouter (delegates to per-repo IStorageService via IRepoContextAccessor)
ServiceRegistry (thread-safe ConcurrentDictionary of IStorageService per repo)

Provider selection in Program.cs:
  "Storage:Provider" → "inmemory" | "sqlite"
  └─ InMemoryVectorStore (Memori) or SqliteVectorStore (SK SqliteVec)
```

### Key Observations

1. **Depends on `Microsoft.Extensions.VectorData`** — CodeMemory is fully abstracted behind the `VectorStore` abstraction. This means any provider implementing `IVectorStore` works automatically (CosmosDB, Redis, Qdrant, etc.).

2. **Flat collection model** — symbols, chunks, and relationships are three independent collections. No graph traversal, no edges, no labels/tags. Queries are filter-based (`Expression<Func<T, bool>>`).

3. **HNSW is delegated** — If the underlying `VectorStore` supports ANN (like SK SqliteVec does), CodeMemory gets it for free. Otherwise (`InMemoryVectorStore`), it's brute-force.

4. **The `IStorageService` interface is the single abstraction** — As long as you implement these 15 methods, you're a storage provider.

---

## Symmetry Map

| Concern | CodeMemory | LiteGraph |
|---|---|---|
| Contract | `IStorageService` (interface) | `GraphRepositoryBase` (abstract class) |
| Factory | `Program.cs` switch | `GraphRepositoryFactory.Create()` |
| In-memory | `InMemoryVectorStore` | `SqliteGraphRepository(InMemory: true)` or `InMemoryGraphRepository` (spike) |
| File SQLite | `SqliteVectorStore` (SK SqliteVec) | `SqliteGraphRepository(InMemory: false)` |
| PostgreSQL | Not available | `PostgresqlGraphRepository` |
| Vector index | Delegated to `IVectorStore` provider | Custom `IVectorIndex` + HNSW |
| Multi-tenancy | `StorageServiceRouter` + `IRepoContextAccessor` | Native `Tenant → Graph` hierarchy |
| Query language | MCP tools only | Native Cypher/GQL-inspired DSL |

---

## Decision Matrix for Enterprise Storage

When we decide to build CodeMemory's enterprise storage layer, these are the viable paths:

### Option A: Implement `IStorageService` directly against PostgreSQL

- **Pro**: Full control, no dependency on LiteGraph or `VectorStore` abstractions
- **Pro**: Can use PostgreSQL's native `pgvector` extension for HNSW
- **Con**: Must implement all 15 methods + vector search manually
- **Con**: No graph query language, no labels/tags, no edge traversal
- **Effort**: Medium
- **Best for**: Teams that only want vector + symbol storage without graph features

### Option B: Use LiteGraph's PostgresqlGraphRepository via LiteGraphClient

- **Pro**: LiteGraph already ships production PostgreSQL support
- **Pro**: Get graph query DSL, HNSW, labels/tags, multi-tenancy free
- **Pro**: Same code path as the in-memory/SQLite modes (swap config only)
- **Con**: Must accept LiteGraph as a dependency (graph model overhead)
- **Con**: Queries go through LiteGraphClient API, not `VectorStore`
- **Effort**: Low — already designed in the PLAN.md
- **Best for**: Teams that want graph RAG features from day one

### Option C: Write a `VectorStore`-compatible PostgreSQL provider

- **Pro**: Plugs into existing `StorageService` without changes
- **Pro**: All `Microsoft.Extensions.VectorData` compatible tools work
- **Con**: No graph model (edges, labels, tags) — still flat collections
- **Con**: Must implement `IVectorStore` contract (collection management, vector search, filtering)
- **Effort**: Medium-High
- **Best for**: Teams that want to stay in the `VectorStore` ecosystem but need PostgreSQL persistence

### Option D: Two-tier — LiteGraph for graph queries + PostgreSQL for vector-native storage

- **Pro**: Best of both: graph DSL from LiteGraph, native `pgvector` performance
- **Con**: Eventual consistency between two stores
- **Con**: Operational complexity of two backends
- **Effort**: High
- **Best for**: Production deployments at scale where HNSW performance and graph features are both critical

---

## Recommendation

**Option B is the pragmatic path forward.** LiteGraphClient wraps `PostgresqlGraphRepository` with the same interface as `SqliteGraphRepository(InMemory: true)` (and eventually `InMemoryGraphRepository` — see `LITEGRAPH-STORAGE-SPIKE.md`). The provider swap is a config change:

```csharp
// Ephemeral dev
DatabaseSettings { Type = Sqlite, InMemory = true }

// File SQLite single-node
DatabaseSettings { Type = Sqlite, Filename = ".memorycode/graph.db" }

// Production PostgreSQL
DatabaseSettings { Type = Postgresql, Hostname = "...", DatabaseName = "codememory", ... }
```

No code changes — just `DatabaseSettings`. This is the same `GraphRepositoryFactory` LiteGraph already ships.

Full implementation would look like:

| Tier | LiteGraph Backend | Data Lifetime |
|---|---|---|
| Dev / CI / Anon | `SqliteGraphRepository(InMemory: true)` or `InMemoryGraphRepository` | Ephemeral — process death = data death |
| Single-user prod | `SqliteGraphRepository(Filename: "...")` | Persisted on disk |
| Enterprise | `PostgresqlGraphRepository(...)` | Production PostgreSQL |
