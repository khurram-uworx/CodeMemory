# PgVector Storage Provider — Implementation Plan

## Purpose

Break the PgVector storage provider feature into concrete, assignable tasks. Each task is small enough for a coding agent to own end-to-end.

Design reference: `docs/PGV-SPIKE.md`

---

## Suggested Execution Order

1. **Task 1**: `PgVectorOptions.cs` — config POCO (prerequisite, no deps)
2. **Task 2**: `PgVectorStore.cs` — `VectorStore` factory (depends on Task 1)
3. **Task 3**: `PgVectorCollection.cs` — full CRUD + vector search (depends on Task 2, biggest task)
4. **Task 4**: `ServiceCollectionExtensions.cs` + `Program.cs` wiring (depends on Task 2+3)
5. **Task 5**: Build + smoke test (depends on all above)

---

## Task 1: PgVectorOptions Configuration POCO

### Priority

High

### Goal

Create the configuration model that drives connection string, schema, and optional vector index settings for all PgVector-backed collections.

### Scope

- Define `PgVectorOptions` record/class with connection string, schema name, and optional `VectorIndexOptions` sub-config
- Define `VectorIndexOptions` record/class for HNSW/IVFFlat index parameters (method, distance function, m, ef_construction, lists)
- Use sensible defaults matching pgvector conventions

### Acceptance criteria

- `PgVectorOptions { string ConnectionString, string Schema, VectorIndexOptions? Index }`
- `VectorIndexOptions { string Method, string DistanceFunction, int? M, int? EfConstruction, int? Lists }`
- Defaults: `Schema = "public"`, `Method = "hnsw"`, `DistanceFunction = "vector_cosine_ops"`, `M = 16`, `EfConstruction = 64`
- Proper XML documentation on all properties

### Files likely involved

- `src/CodeMemory.AspNet/Storage/PgVector/PgVectorOptions.cs` (new)

---

## Task 2: PgVectorStore — VectorStore Factory

### Priority

High

### Goal

Implement the `VectorStore` abstract class that manages the `NpgsqlDataSource` and creates typed `PgVectorCollection` instances for each collection name.

### Scope

- `PgVectorStore : VectorStore` with:
  - Constructor taking connection string + `PgVectorOptions`
  - Creates `NpgsqlDataSource` with `UseVector()` registration
  - `GetCollection<TKey, TRecord>(name, definition?)` returns `PgVectorCollection<TKey, TRecord>`
  - `GetDynamicCollection` throws `NotSupportedException`
  - `ListCollectionNamesAsync` returns the 3 known collection names
  - `CollectionExistsAsync` delegates to SQL: `SELECT EXISTS (SELECT FROM pg_tables WHERE schemaname = @schema AND tablename = @name)`
  - `EnsureCollectionDeletedAsync` runs `DROP TABLE IF EXISTS`
  - `Dispose` disposes the `NpgsqlDataSource`
- Thread-safe collection caching in `ConcurrentDictionary<string, object>` (same pattern as Memori's InMemoryVectorStore)
- Type enforcement: if a collection already exists with a different TKey/TRecord, throw `VectorStoreException`

### Acceptance criteria

- `GetCollection<string, SymbolRecord>("symbols")` returns `PgVectorCollection<string, SymbolRecord>`
- `GetCollection<string, ChunkRecord>("chunks", definition)` passes definition through to collection constructor
- Same collection name with different types throws `VectorStoreException`
- `Dispose` disposes the data source
- `EnsureCollectionDeletedAsync("symbols")` runs `DROP TABLE IF EXISTS public.symbols`

### Files likely involved

- `src/CodeMemory.AspNet/Storage/PgVector/PgVectorStore.cs` (new)

---

## Task 3: PgVectorCollection — CRUD + Vector Search

### Priority

High

### Goal

Implement the `VectorStoreCollection<TKey, TRecord>` abstract class with full PostgreSQL-backed CRUD, vector similarity search via pgvector's `<=>` operator, and client-side expression filtering for filtered reads.

### Scope

#### 3a: Schema reflection

- Static reflection fields computed once per closed generic type (same pattern as Memori's `VectorStoreSchema`):
  - `KeyProperty` — property with `[VectorStoreKey]`
  - `VectorProperty` — property with `[VectorStoreVector]` (nullable; only `ChunkRecord` has one)
  - `DataProperties` — all properties with `[VectorStoreData]`
- Fall back to `VectorStoreCollectionDefinition` if passed from `GetCollection`
- Map .NET types to PostgreSQL column types: `string → TEXT`, `int → INTEGER`, `ReadOnlyMemory<float>? → vector(n)`, etc.

#### 3b: DDL — `EnsureCollectionExistsAsync`

```sql
CREATE TABLE IF NOT EXISTS {schema}.{tableName} (
    Id TEXT PRIMARY KEY,
    {data columns},
    Embedding vector({dimension})   -- only if VectorProperty exists
);
```

Optional HNSW/IVFFlat index based on `PgVectorOptions.Index`.

#### 3c: CRUD operations

| Method | SQL |
|--------|-----|
| `GetAsync(TKey key)` | `SELECT * FROM {t} WHERE Id = @key` |
| `GetAsync(IEnumerable<TKey> keys)` | `SELECT * FROM {t} WHERE Id = ANY(@keys)` |
| `UpsertAsync(TRecord record)` | `INSERT INTO {t}({cols}) VALUES({params}) ON CONFLICT (Id) DO UPDATE SET {updates}` |
| `UpsertAsync(IEnumerable<TRecord>)` | Same per record in a loop (or batch if Npgsql supports multi-row INSERT) |
| `DeleteAsync(TKey key)` | `DELETE FROM {t} WHERE Id = @key` (idempotent — succeed if not found) |
| `DeleteAsync(IEnumerable<TKey>)` | `DELETE FROM {t} WHERE Id = ANY(@keys)` (idempotent) |

#### 3d: Vector search — `SearchAsync<TInput>`

- If `TInput` is `ReadOnlyMemory<float>`:
  - Convert to `Pgvector.Vector`
  - Run `SELECT *, Embedding <=> @query AS __score FROM {t} ORDER BY Embedding <=> @query LIMIT @top`
  - Map results to `VectorSearchResult<TRecord>` with `Score = 1 - distance` (cosine distance → similarity)
  - Apply optional `Filter` from `VectorSearchOptions` via client-side pre-filtering
- If `TInput` is `string` → throw `VectorStoreException` (no text search in PgVector v1)
- Return `IAsyncEnumerable<VectorSearchResult<TRecord>>`

#### 3e: Filtered reads — `GetAsync(Expression<Func<TRecord, bool>>, int top, ...)`

- Compile expression with `.Compile()`
- `SELECT * FROM {tableName}`
- Iterate results client-side, apply compiled filter + Skip/Take
- Acceptable performance for repo-scale data (10K–100K records)

#### 3f: Collection lifecycle

- `CollectionExistsAsync` → `SELECT EXISTS (SELECT FROM pg_tables WHERE ...)`
- `EnsureCollectionExistsAsync` → see 3b
- `EnsureCollectionDeletedAsync` → `DROP TABLE IF EXISTS {tableName}`
- Track `volatile bool _deleted` for post-delete guard

### Acceptance criteria

- Can create all 3 tables (symbols, chunks, relationships) with correct column types
- Can upsert and retrieve records by key
- Vector search returns scored results ordered by similarity
- Filtered reads (equality patterns used by `StorageService`) work correctly
- Delete is idempotent
- Thread-safe for concurrent access
- `Pgvector.Vector` conversion handles both null and non-null ReadOnlyMemory<float>?

### Files likely involved

- `src/CodeMemory.AspNet/Storage/PgVector/PgVectorCollection.cs` (new)

---

## Task 4: DI Wiring + Program.cs Integration

### Priority

High

### Goal

Wire the PgVector provider into the ASP.NET host's storage registration and configuration.

### Scope

- Create `ServiceCollectionExtensions.cs` with `AddCodeMemoryPgVectorStorage()` extension
- Modify `CodeMemory.AspNet/Program.cs`:
  - Read `PgVectorOptions` from configuration
  - Add `"pgvector"` branch in the per-repo storage registration loop (alongside existing `"inmemory"` / `"sqlite"`)
  - Skip `CollectionRegistry`/`SqlQueryService`/`TableSchemaProvider` registration for PgVector (same as SQLite)
- Add `appsettings.json` entries for PgVector configuration

### Acceptance criteria

- Setting `Storage:Provider: "pgvector"` with valid connection string creates PgVector-backed storage
- Existing `"inmemory"` and `"sqlite"` providers continue working unchanged
- `SqlQueryService` is not registered for PgVector provider
- All 4 existing MCP tools (ping, related code, semantic search, etc.) work with PgVector backend

### Files likely involved

- `src/CodeMemory.AspNet/Storage/PgVector/ServiceCollectionExtensions.cs` (new)
- `src/CodeMemory.AspNet/Program.cs` (modify)
- `src/CodeMemory.AspNet/appsettings.json` (modify)

---

## Task 5: Build + Smoke Test

### Priority

High

### Goal

Verify the PgVector storage provider compiles, runs, and passes basic functionality checks.

### Scope

- `dotnet build` on `CodeMemory.AspNet` succeeds
- Spot-check the 3 critical paths compile correctly:
  - `PgVectorStore.GetCollection<TKey,TRecord>` returns typed collection
  - `PgVectorCollection.UpsertAsync` builds correct SQL
  - `PgVectorCollection.SearchAsync` builds correct SQL with `<=>` operator
- Verify no regressions in existing providers (`dotnet build` on `CodeMemory.Mcp` and `CodeMemory` succeeds)

### Acceptance criteria

- `dotnet build src/CodeMemory.AspNet/CodeMemory.AspNet.csproj` succeeds with no errors
- `dotnet build src/CodeMemory/CodeMemory.csproj` succeeds (no changes, still clean)
- `dotnet build src/CodeMemory.Mcp/CodeMemory.Mcp.csproj` succeeds (no changes, still clean)

### Files likely involved

- All files from Tasks 1–4

---

## Agent Handoff Batches

### Batch A (sequential — each depends on prior)

1. **Task 1** — `PgVectorOptions.cs` (trivial, ~15 min)
2. **Task 2** — `PgVectorStore.cs` (~30 min)
3. **Task 3** — `PgVectorCollection.cs` (~2 hr, largest)

### Batch B (parallel-ready after Batch A)

4. **Task 4** — DI wiring + Program.cs (~30 min)
5. **Task 5** — Build + verify (~15 min)

---

## Final Checklist

- [x] Every task has a clear owner-sized scope
- [x] Every task has acceptance criteria
- [x] Likely files are listed to reduce agent search time
- [x] Execution order reflects real dependencies
- [x] Design decisions documented in `docs/PGV-SPIKE.md`
