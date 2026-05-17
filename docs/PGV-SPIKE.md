# PgVector Storage Provider — Spike Assessment

## Purpose

Assess feasibility and design a `PgVector`-backed `VectorStore` implementation for CodeMemory's ASP.NET multi-repo host, replacing the ephemeral `InMemoryVectorStore` with persistent PostgreSQL + pgvector storage.

---

## Date

2026-05-17

## Participants

AI coding agent + human review.

---

## Motivation

CodeMemory uses Memori's `InMemoryVectorStore` as the default vector store. While convenient (zero-dependency, out-of-box), it has:

| Issue | Consequence |
|-------|-------------|
| **Data lost on restart** | In-memory store vanishes. Re-indexing on every startup is wasteful for large repos |
| **Single-process scale** | Cannot share index across multiple server instances |
| **No native SQL** | Requires `SqlQueryService` hack that translates SQL → LINQ expressions → client-side filter |
| **Memori coupling** | The store is tied to Memori's `InMemoryVectorStore` class in a package that's not the team's own |

For the ASP.NET multi-repo server, PostgreSQL + pgvector provides:
- Persistent, durable storage
- Shared across processes/instances
- Native SQL with pgvector distance operators
- Connection pooling via `NpgsqlDataSource`
- Schema isolation per repo

---

## Research Sources

### Source 1: Memori — `InMemoryVectorStore`

**Location:** `E:\khurram-uworx\Memori\src\Memori\Storage\InMemoryVectorStore.cs` (573 lines)

**What it is:** A reference implementation of the `Microsoft.Extensions.VectorData` abstractions.

**Architecture:**

```
VectorStore (abstract, MEVD)
  └── InMemoryVectorStore : VectorStore          [sealed]
        └── GetCollection<TKey, TRecord>(name)   [factory]

VectorStoreCollection<TKey, TRecord> (abstract, MEVD)
  └── InMemoryVectorStoreCollection<TKey,TRecord> : VectorStoreCollection<TKey,TRecord>  [file-scoped sealed]
```

**Key internals:**
- `ConcurrentDictionary<string, object>` for collections (store level)
- `ConcurrentDictionary<TKey, TRecord>` for records (collection level)
- `volatile bool deleted` flag per collection
- Static reflection fields (`keyProperty`, `vectorProperty`, `textProperties`) computed once per closed generic type via `VectorStoreSchema`

**CRUD:**
| Method | Implementation |
|--------|---------------|
| `UpsertAsync(TRecord)` | Extract key via reflection, `records[key] = record` |
| `UpsertAsync(IEnumerable)` | Same per record |
| `GetAsync(TKey)` | `records.TryGetValue` |
| `GetAsync(Expression, int, ...)` | Compile expression, iterate, filter, take |
| `DeleteAsync(TKey)` | `records.TryRemove` |
| `CollectionExistsAsync` | `!deleted` |
| `EnsureCollectionExistsAsync` | No-op (always exists) |
| `EnsureCollectionDeletedAsync` | Clear dictionary, `deleted = true` |

**Vector search** (`SearchAsync<TInput>`):
- If `TInput` is `ReadOnlyMemory<float>`: iterate all records, compute `TensorPrimitives.CosineSimilarity`, sort by score descending, apply `Skip`/`ScoreThreshold`
- If `TInput` is `string`: token overlap (Jaccard-like) against full-text indexed properties

**NuGet dependencies:**
| Package | Version | Role |
|---------|---------|------|
| `Microsoft.Extensions.VectorData.Abstractions` | 10.1.0 | `VectorStore`, `VectorStoreCollection`, attributes, options |
| `Microsoft.Extensions.AI` | 10.5.2 | `IEmbeddingGenerator` |
| `System.Numerics.Tensors` | 10.0.7 | `TensorPrimitives.CosineSimilarity` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | — | DI |

---

### Source 2: pgvector-dotnet

**Location:** `E:\github\pgvector-dotnet`

**What it is:** A low-level Npgsql type plugin for the pgvector PostgreSQL extension. **No high-level `IVectorStore` or `VectorStore` abstraction.**

**NuGet packages:**

| Package | Version | Depends on |
|---------|---------|------------|
| `Pgvector` | 0.3.2 | Npgsql 8.0.5 |
| `Pgvector.Dapper` | 0.3.1 | Dapper 2.0.4 |
| `Pgvector.EntityFrameworkCore` | 0.3.0 | Npgsql.EntityFrameworkCore.PostgreSQL 9.0.1 |

**Core type — `Pgvector.Vector`:**

```csharp
public class Vector : IEquatable<Vector>
{
    public ReadOnlyMemory<float> Memory { get; }
    public Vector(ReadOnlyMemory<float> v);    // from float[]
    public Vector(string s);                    // from "[1,2,3]"
    public float[] ToArray();
    public override string ToString();          // "[1,2,3]"
}
```

Also provides `HalfVector` (float16) and `SparseVector`.

**Npgsql integration:**

```csharp
// Single entry point — registers all three type mappings
dataSourceBuilder.UseVector();
```

Registers `VectorConverter` (extends `PgStreamingConverter<Vector>`):
- **Read:** `ushort dim` → `ushort unused` → `dim` × float32
- **Write:** `ushort dim` → `ushort 0` → float32 × dim

**Key observations:**
- Non-nullable `ReadOnlyMemory<float>` — matches CodeMemory's `ChunkRecord.Embedding` type
- Max dimension: 65535 (enforced by `ushort` wire protocol)
- The library is narrowly focused on type mapping + EF Core LINQ translation
- No concept of "collections", "CRUD", "vector search orchestration" — those are raw SQL via `NpgsqlCommand`

---

### Source 3: Microsoft.Extensions.VectorData Abstractions

**Docs:** https://learn.microsoft.com/dotnet/ai/conceptual/mevd-library

**Package:** `Microsoft.Extensions.VectorData.Abstractions` (v10.x, already in CodeMemory.csproj)

**Core hierarchy:**

```
VectorStore (abstract, IDisposable)
  ├── GetCollection<TKey, TRecord>(name, definition?) → VectorStoreCollection<TKey, TRecord>
  ├── GetDynamicCollection(name, definition) → VectorStoreCollection<object, Dictionary<string, object?>>
  ├── ListCollectionNamesAsync() → IAsyncEnumerable<string>
  ├── CollectionExistsAsync(name) → Task<bool>
  └── EnsureCollectionDeletedAsync(name) → Task

VectorStoreCollection<TKey, TRecord> (abstract, IVectorSearchable<TRecord>, IDisposable)
  where TKey : notnull, TRecord : class
  ├── Name → string
  ├── CollectionExistsAsync() → Task<bool>
  ├── EnsureCollectionExistsAsync() → Task
  ├── EnsureCollectionDeletedAsync() → Task
  ├── GetAsync(TKey key, options?, ct) → Task<TRecord?>
  ├── GetAsync(IEnumerable<TKey> keys, options?, ct) → IAsyncEnumerable<TRecord>
  ├── GetAsync(Expression<Func<TRecord, bool>>, int top, options?, ct) → IAsyncEnumerable<TRecord>
  ├── DeleteAsync(TKey key, ct) → Task
  ├── DeleteAsync(IEnumerable<TKey> keys, ct) → Task
  ├── UpsertAsync(TRecord record, ct) → Task
  ├── UpsertAsync(IEnumerable<TRecord> records, ct) → Task
  └── SearchAsync<TInput>(TInput, int top, VectorSearchOptions<TRecord>?, ct) → IAsyncEnumerable<VectorSearchResult<TRecord>>
```

**Attributes for data model annotation:**
| Attribute | Purpose |
|-----------|---------|
| `[VectorStoreKey]` | Primary key (`IsAutoGenerated`, `StorageName`) |
| `[VectorStoreData]` | Data property (`IsIndexed`, `IsFullTextIndexed`, `StorageName`) |
| `[VectorStoreVector]` | Vector property (`Dimensions`, `IndexKind`, `DistanceFunction`, `StorageName`) |

**Key design rules from MS docs:**
1. `GetCollection` should not verify collection existence — just construct and return the collection object
2. `DeleteAsync(single key)` should succeed if record does not exist (idempotent)
3. `DeleteAsync(multiple keys)` should succeed if any records don't exist (idempotent)
4. Implementations must be thread-safe

---

### Source 4: CodeMemory Storage Abstractions

**Location:** `E:\khurram-uworx\CodeMemory\src\CodeMemory\Storage\`

#### `IStorageService` (interface — 15 methods)

```csharp
public interface IStorageService
{
    string RepoRoot { get; }
    VectorStore? Store { get; }
    Task InitializeAsync(CancellationToken);
    Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord>, CancellationToken);
    Task StoreChunksAsync(IReadOnlyList<ChunkRecord>, CancellationToken);
    Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord>, CancellationToken);
    Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken);
    Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken);
    Task<RelationshipRecord?> GetRelationshipAsync(string id, CancellationToken);
    Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(string filePath, int top, CancellationToken);
    Task<IReadOnlyList<SymbolRecord>> GetSymbolsByKindAsync(string kind, int top, CancellationToken);
    Task<IReadOnlyList<ChunkRecord>> GetChunksBySymbolAsync(string symbolId, CancellationToken);
    Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsBySourceAsync(string sourceSymbolId, CancellationToken);
    Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsByTargetAsync(string targetSymbolId, CancellationToken);
    Task<IReadOnlyList<ScoredChunk>> SearchChunksAsync(ReadOnlyMemory<float> embedding, int top, CancellationToken);
    Task ClearAllAsync(CancellationToken);
}
```

#### `StorageService` — concrete implementation

- Constructed with `VectorStore` (abstract) + optional `IEmbeddingGenerator`
- `InitializeAsync()` calls `GetCollection<string, T>(name)` for each of 3 collections, then `EnsureCollectionExistsAsync`
- `ChunkRecord` uses runtime `VectorStoreCollectionDefinition` from `VectorSchema.CreateChunkDefinition(dimension)` to override embedding dimension
- Filtered reads use `Expression<Func<T, bool>>` passed to `GetAsync()` — all simple equality patterns:
  ```csharp
  s => s.FilePath == filePath
  s => s.Kind == kind
  c => c.SymbolId == symbolId
  r => r.SourceSymbolId == sourceSymbolId
  r => r.TargetSymbolId == targetSymbolId
  ```
- Vector search calls `chunks!.SearchAsync<ReadOnlyMemory<float>>(embedding, top, ...)`

#### Storage models

Three sealed records with `[VectorStoreKey]`, `[VectorStoreData]`, `[VectorStoreVector]` attributes.

```csharp
public sealed class SymbolRecord   { string Id, Name, Kind, FilePath, FullName; int LineStart, LineEnd; string? Modifiers, Documentation; }
public sealed class ChunkRecord    { string Id, SymbolId, FilePath, Content, Language; int LineStart, LineEnd; string? MetadataJson; ReadOnlyMemory<float>? Embedding; }
public sealed class RelationshipRecord { string Id, SourceSymbolId, TargetSymbolId, RelationshipType; }
```

**Only `ChunkRecord` has an embedding.** Dimension is defined at runtime via `VectorSchema.CreateChunkDefinition(dimension)`.

#### VectorSchema

```csharp
public static class VectorSchema
{
    public static VectorStoreCollectionDefinition CreateChunkDefinition(int dimension)
    {
        return new() {
            Properties = [
                new VectorStoreKeyProperty("Id", typeof(string)),
                new VectorStoreDataProperty("SymbolId", typeof(string)),
                new VectorStoreDataProperty("FilePath", typeof(string)),
                new VectorStoreDataProperty("Content", typeof(string)),
                new VectorStoreDataProperty("Language", typeof(string)),
                new VectorStoreDataProperty("LineStart", typeof(int)),
                new VectorStoreDataProperty("LineEnd", typeof(int)),
                new VectorStoreDataProperty("MetadataJson", typeof(string)) { StorageName = "metadata" },
                new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), dimension)
                    { DistanceFunction = DistanceFunction.CosineDistance }
            ]
        };
    }
}
```

#### Current provider selection (Program.cs)

`appsettings.json` → `Storage:Provider` → `"inmemory"` (default, with SQL query support) or `"sqlite"` (no SQL query tool).


Three DI registration paths exist:
1. **`CodeMemory.Mcp`** — hardcoded `InMemoryVectorStore` via `AddCodeMemoryInMemoryStorage()` (no config)
2. **`CodeMemory.AspNet` — inmemory** — `new InMemoryVectorStore()` per repo
3. **`CodeMemory.AspNet` — sqlite** — `new SqliteVectorStore(connectionString)` per repo (only in test infra effectively)

#### SqlQueryService (InMemoryVectorStore only)

`SqlQueryService` is a workaround: it parses user SQL via SqlParserCS, translates WHERE clauses to LINQ `Expression<Func<T,bool>>` via `SqlExpressionBuilder`, then calls `GetAsync(expression, ...)` on the InMemoryVectorStore collection via reflection.

**It is explicitly tied to InMemoryVectorStore** — the SQLite backend throws `NotSupportedException("InMemoryVectorStore backend required for SQL queries")`. This is by design: PostgreSQL already speaks SQL natively.

---

#### Multi-repo routing

```
ASP.NET request → /api/mcp/{repoName}
  → ConfigureSessionOptions extracts repo name → sets IRepoContextAccessor.CurrentRepoName
  → StorageServiceRouter delegates to per-repo IStorageService (looked up from ServiceRegistry)
```

All higher-level services (DependencyGraph, Architecture, GitHistory) are singletons that resolve `IStorageService` → `StorageServiceRouter`.

---

## Design Discussion

### Question 1: Where should the PgVector implementation live?

**Options considered:**
1. `CodeMemory` library (alongside storage abstractions) — would need Npgsql dependency in the core library
2. `CodeMemory.Storage` — would need a new project referencing both `CodeMemory` and `Pgvector`/`Npgsql`
3. `CodeMemory.AspNet` — already has `Pgvector` NuGet reference, server-only concern, makes sense for multi-repo central server

**Decision:** ✅ **`CodeMemory.AspNet/Storage/PgVector/`** — The PgVector provider is a server-side concern. The core `CodeMemory` library stays pure with no database driver dependencies. STDIO MCP (`CodeMemory.Mcp`) continues using `InMemoryVectorStore`.

### Question 2: Implement VectorStore abstractions or a standalone IStorageService?

**Options considered:**
1. **Direct `IStorageService` implementation** — simpler, but breaks `SqlQueryService` (which uses `VectorStore` directly) and means `StorageService.Store` returns null
2. **Full `VectorStore` + `VectorStoreCollection<TKey,TRecord>`** — `StorageService` works unchanged. The `Store` property returns the store. All tools work transparently.

**Decision:** ✅ **Full `VectorStore`/`VectorStoreCollection` abstract class implementation** — This gives transparent compatibility. `StorageService`, `SqlQueryService` (via reflection), and all MCP tools continue working without changes. The implementation follows the same pattern as Memori's `InMemoryVectorStore`.

### Question 3: How to handle `GetAsync(Expression<Func<TRecord,bool>>, ...)`?

This is the filtered-read method called by `StorageService` for 5 of its 15 methods. Options:

| Option | LOC | Complexity | Performance |
|--------|-----|------------|-------------|
| **A:** ExpressionVisitor → SQL WHERE clause | ~250 | High | Best |
| **B:** Compile expression, fetch all, client-side filter | ~20 | Trivial | OK for repo-scale |
| **C:** Recognize 5 equality patterns, build simple SQL | ~80 | Medium | Good |

The human reviewer noted: *"PostgreSQL supports SQL itself, long term plan is for ASP.NET Project where PostgreSQL storage will be used, we can introduce PostgreSQL-specific SQL querying tool"* — and a full ExpressionVisitor is not needed.

**Decision:** ✅ **Option B — client-side expression matching.** Compile the expression, `SELECT * FROM table`, filter in-memory. At repo scale (10K–100K records), this is fast enough for indexing-time filtered reads. A future `pgvector_sql_query` MCP tool will bypass VectorStore entirely and execute SQL directly on the repo's Npgsql connection.

### Question 4: What about the existing SqlQueryService?

The current `SqlQueryService` is an InMemoryVectorStore-specific workaround. For PgVector, it is not registered (same as the SQLite backend).

**Decision:** ✅ **Do not register `SqlQueryService` for PgVector.** The PgVector provider in `Program.cs` skips `CollectionRegistry`/`SqlQueryService`/`TableSchemaProvider` registration. A future `pgvector_sql_query` MCP tool (in `CodeMemory.AspNet/Tools/`) will bypass VectorStore entirely and execute SQL directly against PostgreSQL.

### Question 5: Connection management

**Decision:** ✅ **One `NpgsqlDataSource` per repo**, stored in `PgVectorStore`. The data source is created with the connection string + `UseVector()` registration. Matches the current per-repo storage instance pattern in `Program.cs`.

---

## Proposed Architecture

### Directory structure

```
src/CodeMemory.AspNet/Storage/PgVector/
├── PgVectorStore.cs                   — VectorStore implementation (factory)
├── PgVectorCollection.cs              — VectorStoreCollection<TKey,TRecord> implementation
├── PgVectorOptions.cs                 — Configuration POCO
└── ServiceCollectionExtensions.cs     — DI registration
```

### Class design

```
PgVectorOptions
{
    string ConnectionString          // Npgsql connection string
    string Schema = "public"         // PostgreSQL schema name
    int? VectorDimensions = null     // Override for ChunkRecord embedding dim
    VectorIndexOptions? Index        // Optional HNSW/IVFFlat index params
}

VectorStore (abstract, MEVD)
  └── PgVectorStore : VectorStore
        └── NpgsqlDataSource _dataSource
        └── PgVectorOptions _options
        └── GetCollection<TKey, TRecord>(name, definition?)
            └── Creates PgVectorCollection<string, SymbolRecord|ChunkRecord|RelationshipRecord>

VectorStoreCollection<TKey, TRecord> (abstract, MEVD)
  └── PgVectorCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>
        └── NpgsqlDataSource _dataSource
        └── PgVectorOptions _options
        └── string _tableName              // e.g. "symbols", "chunks", "relationships"
        └── VectorStoreCollectionDefinition _definition  // column metadata
```

### SQL mappings

#### DDL — `EnsureCollectionExistsAsync`

```sql
CREATE TABLE IF NOT EXISTS {schema}.{tableName} (
    Id TEXT PRIMARY KEY,
    -- data columns from VectorStoreDataProperty attributes
    Embedding vector({dimension})   -- only for ChunkRecord
);
```

Optional HNSW index:
```sql
CREATE INDEX IF NOT EXISTS idx_{tableName}_embedding
    ON {schema}.{tableName}
    USING hnsw (Embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);
```

#### CRUD — `UpsertAsync`

```sql
INSERT INTO {schema}.{tableName} ({columns})
VALUES ({parameterized})
ON CONFLICT (Id) DO UPDATE SET {updates};
```

#### Read — `GetAsync(TKey key)`

```sql
SELECT * FROM {schema}.{tableName} WHERE Id = @key;
```

#### Filtered read — `GetAsync(Expression, int top, ...)`

```
SELECT * FROM {schema}.{tableName};
    → filter client-side via compiled expression
    → apply Skip/Take
```

#### Vector search — `SearchAsync(ReadOnlyMemory<float>, int top, ...)`

```sql
SELECT *, Embedding <=> @query AS __score
FROM {schema}.{tableName}
ORDER BY Embedding <=> @query
LIMIT @top;
```

Supports optional `Filter` from `VectorSearchOptions` via client-side pre-filtering.

#### Delete — `DeleteAsync(TKey key)`

```sql
DELETE FROM {schema}.{tableName} WHERE Id = @key;
```

### Wiring in `Program.cs`

```csharp
// PgVector configuration
var pgVectorConnectionString = builder.Configuration.GetConnectionString("PgVector");
var pgVectorOptions = builder.Configuration.GetSection("PgVector").Get<PgVectorOptions>() ?? new();

// Per-repo registration
foreach (var (name, path) in repositories ?? [])
{
    if (string.Equals(provider, "pgvector", StringComparison.OrdinalIgnoreCase))
    {
        var store = new PgVectorStore(pgVectorConnectionString, pgVectorOptions with { ... });
        var storageService = new StorageService(repoRoot, loggerFactory, store, embeddingGenerator);
        storageRegistry.Register(name, storageService);
    }
}

// No SqlQueryService registration for pgvector provider
```

### Future MCP tool (not in scope of this spike)

```
pgvector_sql_query tool
  → Accepts raw SQL string
  → Resolves per-repo NpgsqlConnection via IRepoContextAccessor
  → Executes directly against PostgreSQL
  → Supports full PostgreSQL SQL + pgvector operators
  → Returns JSON results
```

This avoids any VectorStore abstraction overhead and gives agents full SQL power against the indexed data.

---

## Implementation Plan

### Files to create (5 new files, ~500 LOC total)

| # | File | LOC | Responsibility |
|---|------|-----|----------------|
| 1 | `PgVectorOptions.cs` | ~30 | Configuration POCO: connection string, schema, index options |
| 2 | `PgVectorStore.cs` | ~100 | `VectorStore` factory: creates `NpgsqlDataSource`, `GetCollection` returns `PgVectorCollection` |
| 3 | `PgVectorCollection.cs` | ~350 | `VectorStoreCollection<TKey,TRecord>`: full CRUD via Npgsql + client-side expression filter + pgvector `<=>` search |
| 4 | `ServiceCollectionExtensions.cs` | ~30 | DI extension method |
| 5 | Modify `Program.cs` | +15 | Add `"pgvector"` branch in storage registration |

### Key implementation details

#### PgVectorCollection — Constructor

```csharp
internal sealed class PgVectorCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    readonly NpgsqlDataSource _dataSource;
    readonly string _tableName;
    readonly PgVectorOptions _options;
    readonly VectorStoreCollectionDefinition _definition;
    volatile bool _deleted;

    // Static reflection (computed per closed generic type)
    static readonly PropertyInfo KeyProperty;
    static readonly PropertyInfo? VectorProperty;
    static readonly IReadOnlyList<PropertyInfo> DataProperties;
}
```

#### Reflection schema discovery

Same approach as Memori's `VectorStoreSchema`: use `VectorStoreCollectionDefinition` (passed from `GetCollection`) or fall back to attribute-based discovery via `[VectorStoreKey]`, `[VectorStoreData]`, `[VectorStoreVector]`.

This drives:
- `CREATE TABLE` column list + types
- Parameterized SQL generation for upsert/read
- `Pgvector.Vector` ↔ `ReadOnlyMemory<float>` conversion for embedding column

#### Thread safety

- `NpgsqlDataSource` is thread-safe by design (connection pooling)
- `volatile bool _deleted` flag (same as Memori pattern)
- All async methods are safe for concurrent invocation

---

## Limitations

| Constraint | Reason |
|------------|--------|
| **No SQL query tool for PgVector (v1)** | `SqlQueryService` is InMemoryVectorStore-specific. PostgreSQL needs its own `pgvector_sql_query` tool bypassing VectorStore abstraction |
| **Client-side filtered reads** | `SELECT *` + client-side `Where(compiled).Take(top)`. Acceptable for repo-scale data (10K–100K records). If performance becomes an issue, add an `ExpressionVisitor` later |
| **Schema locked to Codememory models** | The DDL is hardcoded for `SymbolRecord`/`ChunkRecord`/`RelationshipRecord` columns. Generic schema inference from `VectorStoreCollectionDefinition` would be needed for arbitrary types |
| **Pgvector.Vector only** | No `HalfVector` or `SparseVector` support. CodeMemory only uses `ReadOnlyMemory<float>` embeddings |
| **PostgreSQL connection required** | Not suitable for single-repo STDIO MCP usage. `CodeMemory.Mcp` continues using InMemoryVectorStore |
| **No migration/versioning** | Schema created fresh via `CREATE TABLE IF NOT EXISTS`. No ALTER TABLE support for schema evolution |

---

## What Is Not Changing

- **`IStorageService`** — no changes
- **`StorageService`** — no changes (consumes `VectorStore` abstract class)
- **`VectorSchema`** — no changes (used by `StorageService.InitializeAsync`, not store-specific)
- **`SqlQueryService`** — no changes (not registered for PgVector backend)
- **`StorageServiceRouter`** — no changes
- **`ServiceRegistry`** — no changes
- **All existing MCP tools** — no changes
- **`CodeMemory.Mcp` (STDIO)** — stays on InMemoryVectorStore
- **Configuration structure** — additive only (new `Storage.Provider: "pgvector"` value)
- **CodeMemory.csproj** — no new dependencies (Pgvector lives in AspNet)

---

## Effort Summary

| Phase | Scope | Est. LOC | Est. Time |
|-------|-------|----------|-----------|
| 1 | PgVectorOptions + PgVectorStore (factory, connection, DDL) | ~130 | 0.5 day |
| 2 | PgVectorCollection (CRUD, vector search, filtered reads) | ~350 | 1 day |
| 3 | DI wiring + Program.cs integration + smoke test | ~50 | 0.5 day |
| **Total** | **Full provider** | **~530 LOC** | **~2 days** |

---

## References

- Memori InMemoryVectorStore: `E:\khurram-uworx\Memori\src\Memori\Storage\InMemoryVectorStore.cs`
- pgvector-dotnet: `E:\github\pgvector-dotnet\src\Pgvector\`
- MEVD docs: https://learn.microsoft.com/dotnet/ai/conceptual/mevd-library
- Build your own Vector Store connector: https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/how-to/build-your-own-connector
- CodeMemory IStorageService: `src/CodeMemory/Storage/IStorageService.cs`
- CodeMemory StorageService: `src/CodeMemory/Storage/StorageService.cs`
- CodeMemory VectorSchema: `src/CodeMemory/Storage/VectorSchema.cs`
- CodeMemory SqlQueryService: `src/CodeMemory/SqlQuery/SqlQueryService.cs`
