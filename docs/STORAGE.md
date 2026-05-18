# Storage Architecture Plan — ASP.NET (Enterprise)

## Scope Constraint

**This entire plan is exclusively for `CodeMemory.AspNet`.** The `CodeMemory` library (pure library, no ASP.NET dependency) and `CodeMemory.Mcp` (STDIO MCP host) are completely untouched. Zero file changes, zero package changes, zero behavior changes in those projects. Everything described below lives in `src/CodeMemory.AspNet/`.

## Problem

CodeMemory has two deployment targets with fundamentally different storage needs:

| | Mcp (STDIO) | AspNet (HTTP) |
|---|---|---|
| **Purpose** | Single-repo agent tool | Multi-repo enterprise service |
| **Storage** | Everything in memory | Needs persistence, SQL, analytics |
| **Config** | Zero (`--repo <path>`) | `appsettings.json` with repos, connection strings |
| **Chunks** | In-memory vector store | Searchable, persistent |
| **Symbols/Relationships** | In-memory collections | Queriable via SQL (GROUP BY, JOIN, etc.) |

Currently, *both* paths use the same `StorageService` wrapping a `VectorStore` — symbols, relationships, and chunks all live in the same abstracted collections. This works for in-memory but hits ceilings on the AspNet path:

1. **`sql_query`** is locked to `InMemoryVectorStore` — the custom SQL parser (`CodeMemory.Mcp/SqlQuery/SqlQueryService.cs`) builds LINQ expression trees against `VectorStoreCollection`. `PostgresVectorStore` and `SqlServerVectorStore` do not support this.

2. **Structured data in vector storage** — `SymbolRecord` and `RelationshipRecord` have no vector columns. Storing them in a vector-optimized store wastes the RDBMS's native capabilities (indexes, joins, aggregations).

3. **No SQL analytics** — Want "which files have the most methods?" or "average method length per component?" — these require real SQL.

## Design

### Mcp (STDIO) — Untouched

```
IStorageService
  └─ StorageService (src/CodeMemory/Storage)
       └─ InMemoryVectorStore (Memori)
            ├─ "symbols" collection
            ├─ "chunks" collection (with embeddings)
            └─ "relationships" collection
```

- `sql_query` → `SqlQueryService` custom parser → `InMemoryVectorStore`
- Zero configuration
- Single repo
- All data lost on restart (intentional)

### AspNet — HybridStorageService

```
IStorageService
  └─ HybridStorageService (new, in CodeMemory.AspNet/Storage)
       ├─ VectorStore (Microsoft.Extensions.VectorData)
       │    └─ Chunks collection (upsert, vector search, auto-embedding)
       └─ DbContext (EF Core)
            ├─ Symbols table
            └─ Relationships table
```

**Routing table:**

| IStorageService method | Routes to |
|---|---|
| `StoreSymbolsAsync` | `DbContext.Symbols.AddRange()` |
| `StoreChunksAsync` | `VectorStore.GetCollection<ChunkRecord>("chunks").UpsertAsync()` |
| `StoreRelationshipsAsync` | `DbContext.Relationships.AddRange()` |
| `GetSymbolAsync` | `DbContext.Symbols.FindAsync(id)` |
| `GetChunkAsync` | `VectorStore.GetCollection<ChunkRecord>("chunks").GetAsync(id)` |
| `GetRelationshipAsync` | `DbContext.Relationships.FindAsync(id)` |
| `GetSymbolsByFileAsync` | `DbContext.Symbols.Where(s => s.FilePath == path)` |
| `GetSymbolsByKindAsync` | `DbContext.Symbols.Where(s => s.Kind == kind)` |
| `GetChunksBySymbolAsync` | `chunks.GetAsync(filter: c => c.SymbolId == symbolId)` |
| `GetRelationshipsBySourceAsync` | `DbContext.Relationships.Where(r => r.SourceSymbolId == id)` |
| `GetRelationshipsByTargetAsync` | `DbContext.Relationships.Where(r => r.TargetSymbolId == id)` |
| `SearchChunksAsync` | `chunks.SearchAsync(embedding, top, options)` |
| `ClearAllAsync` | Drop + recreate tables and collections |
| `Store` | Returns the `VectorStore` instance |

**Why this split:**

- Symbols and relationships are pure structured data — they belong in a relational database with proper indexes, foreign keys, and SQL query capability
- Chunks need vector search — `Microsoft.Extensions.VectorData` is the right abstraction for this
- `sql_query` on the AspNet path can restrict to `SymbolRecord` and `RelationshipRecord` (via EF Core), leaving chunk vector search to the `semantic_search` MCP tool
- EF Core provides migrations, LINQ queries, and provider abstraction (PostgreSQL or SQL Server)

## Central Wiring: ServiceCollectionExtensions.cs

`src/CodeMemory.AspNet/Storage/ServiceCollectionExtensions.cs` is the single hub that connects everything together, just as it currently handles all storage providers (inmemory, sqlite, pgvector, sqlserver). It absorbs all the complexity so `Program.cs` and other services stay clean.

Currently it:
- Maps `Storage:Provider` values to concrete VectorStore implementations
- Creates per-repo schemas (`CREATE SCHEMA IF NOT EXISTS`)
- Builds `StorageService` instances per repo and returns them

With the hybrid approach, it will:
- Still map `Storage:Provider` values (no new config keys)
- Still create per-repo schemas (same logic, unchanged)
- Create the VectorStore for chunks (same as today)
- **Also** configure EF Core `DbContextOptions` pointing to the same connection + schema
- Return `HybridStorageService` instead of `StorageService` for non-`inmemory` providers
- Keep `inmemory` path returning `StorageService` (unchanged)

`Program.cs` calls `builder.CreateStorage(provider, name, repoRoot, ...)` — it doesn't care whether it gets a `StorageService` or a `HybridStorageService`. The `IStorageService` interface is identical.

## Provider Support

### VectorStore (for chunks)

Configured via existing `Storage:Provider` setting (`appsettings.json`):

| Provider | Implementation | Notes |
|---|---|---|
| `inmemory` | `InMemoryVectorStore` (Memori) | Dev only, no persistence |
| `pgvector` | `PostgresVectorStore` | Production — supported by existing package |
| `sqlserver` | `SqlServerVectorStore` | Production — supported by existing package |
| `sqlite` | `SqliteVectorStore` | Single-server persistence |

### DbContext (for symbols + relationships)

**Decision: Unified single setting.** The existing `Storage:Provider` drives both the VectorStore provider and the EF Core provider. No separate `RelationalProvider` setting.

The mapping:

| `Storage:Provider` value | VectorStore | EF Core (relational) |
|---|---|---|
| `pgvector` | `PostgresVectorStore` | PostgreSQL via `Npgsql.EntityFrameworkCore.PostgreSQL` |
| `sqlserver` | `SqlServerVectorStore` | SQL Server via `Microsoft.EntityFrameworkCore.SqlServer` |
| `sqlite` | `SqliteVectorStore` | SQLite via `Microsoft.EntityFrameworkCore.Sqlite` |
| `inmemory` | `InMemoryVectorStore` (Memori) | No EF Core — falls back to `InMemoryVectorStore` for everything (dev mode, same as Mcp) |

When `Storage:Provider` is `inmemory`, the AspNet path skips `HybridStorageService` and uses the existing `StorageService` (same as Mcp). This keeps dev mode simple.

### Shared Schema — One Schema Per Repository

**Crucial insight: The per-repo schema already exists.** Both `CreatePgVectorStorage` and `createSqlServerStorage` in `ServiceCollectionExtensions.cs` already:
1. Create a schema per repo via `sanitizeSchemaName(repoName)` (e.g., `"codememory"`, `"memori"`)
2. Pass that schema to the VectorStore via `PostgresVectorStoreOptions { Schema = schema }` / `SqlServerVectorStoreOptions { Schema = schema }`

The VectorStore stores chunks in its internal tables within `{schema}.chunks` (or equivalent). EF Core will add `Symbols` and `Relationships` tables to the **same schema**. All three tables coexist in one namespace per repo:

```
Database
└─ Schema: "codememory"
    ├─ (VectorStore internal tables for "chunks" collection)
    ├─ Symbols (EF Core, new)
    └─ Relationships (EF Core, new)
└─ Schema: "memori"
    ├─ (VectorStore internal tables for "chunks" collection)
    ├─ Symbols (EF Core, new)
    └─ Relationships (EF Core, new)
```

This means:
- No new schema isolation mechanism required
- Connection string stays the same (existing `PgVector` / `SqlServer` keys)
- Both stores share the same database transaction scope
- `HybridStorageService.InitializeAsync` creates the schema once (VectorStore path already does this; EF Core just adds its tables)

```json
{
  "Storage": {
    "Provider": "pgvector"
  },
  "ConnectionStrings": {
    "PgVector": "Host=localhost;Database=codememory;Username=...;Password=..."
  },
  "Repositories": {
    "codememory": "..\\..\\",
    "memori": "..\\..\\..\\memori"
  }
}
```

`StorageServiceRouter` maps repo name → schema name via `sanitizeSchemaName(name)`. Same logic as today.

## sql_query Strategy

### Mcp path (unchanged)

- `SqlQueryTool` (`CodeMemory.Mcp/Tools/SqlQueryTool.cs`)
- `SqlQueryService` (`CodeMemory.Mcp/SqlQuery/SqlQueryService.cs`)
- Custom SQL parser → LINQ expression trees → `InMemoryVectorStore`
- Supports all three tables: `SymbolRecord`, `ChunkRecord`, `RelationshipRecord`
- Vector search via `ORDER BY Similarity DESC`

### AspNet path

**Decision: Symbols + Relationships only through AspNetSqlQueryTool.**

New `AspNetSqlQueryTool` in `CodeMemory.AspNet/Tools/`:
- Accepts raw SQL
- Forward SQL directly to the relational database via `DbContext.Database.GetDbConnection()`
- Restricted to `SymbolRecord` and `RelationshipRecord` queries only
- `ChunkRecord` queries return a clear error: "ChunkRecord queries not supported via SQL in this backend. Use semantic_search tool instead."
- Benefits: Clean separation, no custom SQL parser needed, uses the database engine's native SQL
- Limitation: Can't ad-hoc query chunks via SQL (use `semantic_search` tool instead)

## EF Core Model

New entities in `CodeMemory.AspNet/Storage/Models/`:

```csharp
public class SymbolEntity
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Kind { get; set; }
    public string FilePath { get; set; }
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public string FullName { get; set; }
    public string? Modifiers { get; set; }
    public string? Documentation { get; set; }
}

public class RelationshipEntity
{
    public string Id { get; set; }
    public string SourceSymbolId { get; set; }
    public string TargetSymbolId { get; set; }
    public string RelationshipType { get; set; }
}
```

These mirror the existing `SymbolRecord` / `RelationshipRecord` models but without `VectorStore*` attributes.

**Decision: Manual mapping methods.** Static extension methods on entity/record types. No AutoMapper. Simple, no dependencies, easy to debug.

Mapping helpers live in `src/CodeMemory.AspNet/Storage/ModelMapping.cs`:
```csharp
public static SymbolEntity ToEntity(this SymbolRecord r) => new() { ... };
public static SymbolRecord ToRecord(this SymbolEntity e) => new() { ... };
// Same for RelationshipEntity ↔ RelationshipRecord
```

The existing record types stay unchanged (used by VectorStore for chunks and by Mcp path). Chunks have no entity class — they stay in VectorStore only.

### Schema per Repo — DbContext Model Cache

`CodeMemoryDbContext` varies per repo because each repo uses a different schema. EF Core caches models per context type by default, so sharing one `DbContext` type across schemas requires a custom `IModelCacheKeyFactory`:

```csharp
public sealed class CodeMemoryDbContext : DbContext
{
    public string Schema { get; }
    
    public CodeMemoryDbContext(DbContextOptions<CodeMemoryDbContext> options, string schema)
        : base(options)
    {
        Schema = schema;
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        // entity configuration...
    }
}

public sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is CodeMemoryDbContext cm)
            return (context.GetType(), cm.Schema, designTime);
        return context.GetType();
    }
}
```

Registered in DI:
```csharp
services.AddDbContextFactory<CodeMemoryDbContext>((sp, options) =>
{
    var schema = sp.GetRequiredService<IRepoContextAccessor>().CurrentRepoName;
    options.UseNpgsql(connectionString); // or UseSqlServer
});
services.AddSingleton<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();
```

`HybridStorageService` internally resolves `IDbContextFactory<CodeMemoryDbContext>` and passes the schema from its `RepoRoot` or a constructor parameter.

### Indexes

```sql
CREATE INDEX IX_Symbols_FilePath ON symbols (file_path);
CREATE INDEX IX_Symbols_Kind ON symbols (kind);
CREATE INDEX IX_Relationships_Source ON relationships (source_symbol_id);
CREATE INDEX IX_Relationships_Target ON relationships (target_symbol_id);
CREATE INDEX IX_Relationships_Type ON relationships (relationship_type);
```

## Implementation Plan

### Phase 1 — Foundation

1. Add EF Core packages to `CodeMemory.AspNet`:
   - `Microsoft.EntityFrameworkCore`
   - `Microsoft.EntityFrameworkCore.Relational`
   - `Npgsql.EntityFrameworkCore.PostgreSQL`
   - `Microsoft.EntityFrameworkCore.SqlServer`

2. Create entity classes, model mapping, and `CodeMemoryDbContext`

3. Implement `HybridStorageService : IStorageService` in `CodeMemory.AspNet/Storage/`
   - Constructor takes `VectorStore`, `IDbContextFactory<CodeMemoryDbContext>`, `ILogger<HybridStorageService>`, optional `IEmbeddingGenerator`, `int configuredDimension`
   - `IDbContextFactory` is used to create a fresh DbContext per operation (DbContext is not shared/stateful)
   - Routes each `IStorageService` method as shown in the routing table above
   - No changes to `IStorageService` interface
   - `InitializeAsync`: `VectorStoreCollection.EnsureCollectionExistsAsync` + `DbContext.Database.EnsureCreated`
   - Mapping: uses `ModelMapping` static methods internally

4. Update `ServiceCollectionExtensions.CreateStorage` to return `HybridStorageService` instead of `StorageService` for provider-based backends:
   - Still creates schema (existing `CREATE SCHEMA IF NOT EXISTS` logic)
   - Still creates VectorStore instance with per-repo schema
   - **New:** Creates `CodeMemoryDbContext` options + `IDbContextFactory` for the same schema and connection string
   - Returns `HybridStorageService` wrapping both
   - `inmemory` provider path unchanged (still returns `StorageService` fallback)

### Phase 2 — sql_query for AspNet

1. Create `AspNetSqlQueryTool` in `CodeMemory.AspNet/Tools/`
   - Accepts raw SQL
   - Forwards to `DbContext.Database.GetDbConnection()`
   - Restricts to `SymbolRecord` / `RelationshipRecord` queries
   - Returns JSON result set

2. Register alongside existing tools:
   ```csharp
   .WithToolsFromAssembly(typeof(AspNetSqlQueryTool).Assembly)
   ```

### Phase 3 — Future (not immediate)

- Analytics endpoints / dashboard on top of EF Core data
- Migration management for schema changes
- `Microsoft.Extensions.DataIngestion` pipeline integration (when stable)
- Cross-backend chunk SQL queries (Option B above)

## Non-Goals

- **No changes to the `CodeMemory` library project** — no EF Core packages, no new interfaces, no new dependencies. Zero file changes.
- **No changes to `CodeMemory.Mcp` project** — untouched. Mcp stays zero-config, all-in-memory, single-repo.
- **No changes to `IStorageService` interface** — 9 services depend on it; stability is critical.
- **No changes to `IndexingEngine`** — it calls `IStorageService` methods, which `HybridStorageService` routes correctly.
- **No changes to `StorageService`** — the existing `StorageService` in `CodeMemory.Storage` stays as is, still used by Mcp and by AspNet's `inmemory` fallback.

## Decisions

The following decisions have been made for the AspNet storage path:

| Decision | Chosen option | Rationale |
|---|---|---|
| Provider config strategy | **Unified single setting** — `Storage:Provider` drives both VectorStore + EF Core | Simplest configuration. Most common deployment uses one backend. |
| Supported relational backends | **PostgreSQL + SQL Server** — both from day one | Covers both open-source and Microsoft ecosystems. Already partially wired. |
| `sql_query` scope | **Symbols + Relationships only** — Chunks queried via `semantic_search` | Avoids routing complexity. The two query patterns are fundamentally different (SQL vs vector search). |
| Entity-to-record mapping | **Manual static methods** — no AutoMapper | Simple, no dependencies, easy to debug. Minimal boilerplate for two entity types. |
| Per-repo isolation | **Per-repo schemas** — one database, one schema per repo | Same strategy existing PgVector provider already uses. Minimal config, clean separation. |
| Schema migrations | **EnsureCreated + manual sync** — no EF Core migration tooling initially | `DbContext.Database.EnsureCreated()` on startup. Manual SQL scripts for schema changes. Add proper migrations when schema stabilizes. |
| `StorageServiceRouter` | **Unchanged** — each repo gets its own `HybridStorageService` | Router delegates to `IStorageService` per repo name. Works identically with both `StorageService` and `HybridStorageService`. |

---

## Task Breakdown

Tasks follow the template in `docs/TASKS-TEMPLATE.md`. Implementation order reflects real dependencies.

### Suggested Execution Order

1. ~~Task 1: Decision gate (DECIDED, see Decisions section)~~
2. Task 2: Add EF Core packages + create `CodeMemoryDbContext` and entity models
3. Task 3: Implement `HybridStorageService : IStorageService`
4. Task 4: Wire `HybridStorageService` into `Program.cs` + `StorageServiceRouter`
5. Task 5: Implement AspNet `sql_query` tool
6. Task 6: Update `IndexingState` for per-repo completion across both stores

### Coordination Notes

- **Task 1 is DECIDED** — see Decisions section above. All provider choices are recorded.
- **Tasks 3 + 4 share files** — both touch `StorageServiceRouter.cs` and `Program.cs`. Do them sequentially or coordinate carefully.
- **Tasks 5 + 6** are independent of each other once Tasks 3-4 are done.
- **Task 6** is a small but important behavioral fix — `IndexingState` currently tracks a single boolean per repo; the hybrid path needs to confirm both the VectorStore collection and the DbContext table exist.

---

## ~~Task 1: Decision gate — Choose EF Core provider strategy~~ *(DECIDED, see Decisions section)*

All Task 1 decisions are resolved. Captured in the Decisions section above. No further action needed.

### Recorded decisions

- **Provider config:** Unified single setting — `Storage:Provider` drives both VectorStore and EF Core
- **Supported backends:** PostgreSQL + SQL Server (both in day one)
- **Per-repo isolation:** Per-repo schemas (same as existing PgVector strategy)
- **Migrations:** `EnsureCreated` + manual sync (no EF Core migration tooling yet)

---

## Task 2: Add EF Core packages + create `CodeMemoryDbContext` and entity models

### Priority

High

### Goal

Add EF Core dependencies to `CodeMemory.AspNet` and create the `CodeMemoryDbContext` with entity classes for symbols and relationships.

### Scope

- Add NuGet packages to `CodeMemory.AspNet.csproj`:
  - `Microsoft.EntityFrameworkCore` (required)
  - `Microsoft.EntityFrameworkCore.Relational` (required)
  - `Npgsql.EntityFrameworkCore.PostgreSQL` (PostgreSQL provider)
  - `Microsoft.EntityFrameworkCore.SqlServer` (SQL Server provider)
  - `Microsoft.EntityFrameworkCore.Sqlite` (SQLite provider, for local dev)
- Create `src/CodeMemory.AspNet/Storage/Models/` directory with:
  - `SymbolEntity.cs` — mirrors `SymbolRecord` without `VectorStore*` attributes
  - `RelationshipEntity.cs` — mirrors `RelationshipRecord` without `VectorStore*` attributes
- Create `src/CodeMemory.AspNet/Storage/ModelMapping.cs`:
  - Static `ToEntity()` / `ToRecord()` extension methods for both types
- Create `src/CodeMemory.AspNet/Storage/CodeMemoryDbContext.cs`:
  - `DbSet<SymbolEntity> Symbols`
  - `DbSet<RelationshipEntity> Relationships`
  - Override `OnModelCreating` to configure indexes (file_path, kind, source_symbol_id, target_symbol_id)
  - Composite key for relationships (Id matches `$"{SourceSymbolId}->{TargetSymbolId}:{RelationshipType}"` pattern)
  - Configure provider-agnostic schema: table names, column types, indexes — all in `OnModelCreating` so it works with both PostgreSQL and SQL Server
- No migration tooling — use `EnsureCreated` on startup

### Constraints

- Entity property names and types must match the existing `SymbolRecord`/`RelationshipRecord` exactly for clean mapping in `HybridStorageService`
- Do not duplicate chunks in EF Core — chunks stay in VectorStore only

### Acceptance criteria

- `dotnet build` passes for `CodeMemory.AspNet`
- `CodeMemoryDbContext` can be instantiated with a provider-agnostic options builder
- The entity models round-trip the same data as the current `SymbolRecord`/`RelationshipRecord`
- EF Core migration generates correct SQL for the chosen provider

### Files likely involved

- `src/CodeMemory.AspNet/CodeMemory.AspNet.csproj`
- `src/CodeMemory.AspNet/Storage/Models/SymbolEntity.cs` (new)
- `src/CodeMemory.AspNet/Storage/Models/RelationshipEntity.cs` (new)
- `src/CodeMemory.AspNet/Storage/CodeMemoryDbContext.cs` (new)

---

## Task 3: Implement `HybridStorageService : IStorageService`

### Priority

High

### Goal

Implement `HybridStorageService` that routes `IStorageService` method calls between `VectorStore` (chunks) and `DbContext` (symbols + relationships).

### Scope

- Create `src/CodeMemory.AspNet/Storage/HybridStorageService.cs`:
  - Constructor takes `ILogger<HybridStorageService>`, `VectorStore`, `DbContext`, `IEmbeddingGenerator` (optional), `int configuredDimension`
  - Implements all 17 members of `IStorageService`
  - Routing table from `docs/STORAGE.md` (see above)
  - `InitializeAsync`: calls `EnsureCollectionExistsAsync` on the VectorStore chunks collection AND `DbContext.Database.EnsureCreated()` (or migration)
  - `ClearAllAsync`: drops both the VectorStore collection and DbContext tables
  - `Dispose`: disposes VectorStore collections and DbContext
- Mapping helpers to convert between entity types (`SymbolEntity`/`RelationshipEntity`) and record types (`SymbolRecord`/`RelationshipRecord`)

### Constraints

- Must not change the `IStorageService` interface
- Must not reference EF Core in the `CodeMemory` library
- Must be registered as a singleton (in DI) per the existing pattern
- VectorStore dimension validation (from current `StorageService.StoreChunksAsync`) must be preserved

### Acceptance criteria

- All `IStorageService` methods work correctly when:
  - VectorStore is `InMemoryVectorStore` (dev)
  - VectorStore is `PostgresVectorStore` or `SqlServerVectorStore` (production)
- Symbol writes go to `DbContext.Symbols`, not to VectorStore
- Relationship writes go to `DbContext.Relationships`, not to VectorStore
- Chunk writes go to VectorStore collection, not to DbContext
- Vector search (`SearchChunksAsync`) delegates to VectorStore's `SearchAsync`
- `dotnet build` passes

### Files likely involved

- `src/CodeMemory.AspNet/Storage/HybridStorageService.cs` (new)
- `src/CodeMemory/Storage/IStorageService.cs` (read-only reference)
- `src/CodeMemory/Storage/StorageService.cs` (reference for current behavior)
- `src/CodeMemory/Storage/Models.cs` (reference for record types)

---

## Task 4: Wire `HybridStorageService` into `Program.cs` + `StorageServiceRouter`

### Priority

High

### Goal

Register `HybridStorageService` in the AspNet DI container and update `StorageServiceRouter` so each repo gets a per-repo `HybridStorageService` instance.

### Scope

- Update `src/CodeMemory.AspNet/Storage/ServiceCollectionExtensions.cs`:
  - Modify the three factory methods (`CreatePgVectorStorage`, `createSqlServerStorage`, `createSqliteStorage`) to accept an additional schema parameter for EF Core
  - After creating the VectorStore, also create `DbContextOptions<CodeMemoryDbContext>` pointing to the **same connection string and same schema**
  - Return `HybridStorageService` instead of `StorageService` for these provider paths
  - Keep `inmemory` path returning `StorageService` (no EF Core needed)
  - Existing schema creation logic (`CREATE SCHEMA IF NOT EXISTS`) stays — it creates the namespace for both stores simultaneously
- Update `src/CodeMemory.AspNet/Program.cs`:
  - No structural change needed — `CreateStorage` already returns `IStorageService` and registers per-repo via `ServiceRegistry`
  - May need to register `IDbContextFactory<CodeMemoryDbContext>` in DI for `HybridStorageService` to resolve
- No changes to `StorageServiceRouter` — it already delegates to whatever `IStorageService` is registered per repo

### Constraints

- Existing Mcp path (`CodeMemory.Mcp/Program.cs`) must not change
- `StorageServiceRouter` must remain untouched unless HybridStorageService exposes properties differently
- **Per-repo schemas** — one database, one schema per repo. `DbContext` is instantiated per-repo with schema name = sanitized repo name (same logic as `sanitizeSchemaName` in `ServiceCollectionExtensions.cs`)
- Both VectorStore and DbContext operate within the same database when possible (e.g., PostgreSQL with pgvector extension)

### Acceptance criteria

- `CodeMemory.AspNet` starts up with `Storage:Provider: "pgvector"` + relational provider configured
- Each configured repo gets its own `HybridStorageService` with correct per-repo `DbContext`
- Existing tests pass (they use in-memory storage, which should still work)
- `POST /api/mcp/{repo}/tools/call` works for existing tools

### Files likely involved

- `src/CodeMemory.AspNet/Program.cs`
- `src/CodeMemory.AspNet/Storage/ServiceCollectionExtensions.cs`
- `src/CodeMemory.AspNet/Configuration/StorageServiceRouter.cs`
- `src/CodeMemory.AspNet/Configuration/ServiceRegistry.cs`

---

## Task 5: Implement AspNet `sql_query` tool

### Priority

Medium

### Goal

Create an AspNet-specific `sql_query` MCP tool that forwards SQL queries for `SymbolRecord` and `RelationshipRecord` to the relational database, returning JSON results.

### Scope

- Create `src/CodeMemory.AspNet/Tools/AspNetSqlQueryTool.cs`:
  - `[McpServerToolType]` class similar to `SqlQueryTool` but targeting `DbContext` instead of `InMemoryVectorStore`
  - Takes `IStorageService` (resolves to `HybridStorageService`) + `ILogger`
  - On invocation:
    1. Parse the SQL to determine target table (`SymbolRecord` or `RelationshipRecord`)
    2. Reject `ChunkRecord` queries with a clear error: "ChunkRecord queries not supported via SQL in this backend. Use semantic_search tool instead."
    3. Forward the SQL to `DbContext.Database.GetDbConnection()` or use `Database.SqlQueryRaw<T>()`
    4. Return `SqlQueryResult`-shaped JSON (success, rowCount, executionTimeMs, columns, rows, error)
  - Validate SQL is SELECT-only (reject DDL/DML for safety)
- Register in `Program.cs`:
  ```csharp
  .WithToolsFromAssembly(typeof(AspNetSqlQueryTool).Assembly)
  ```
- Tool description must clearly document the table restriction (symbols + relationships only)

### Constraints

- Must not reference `CodeMemory.Mcp.SqlQuery` — this is a separate implementation
- Must not modify the existing `SqlQueryTool` or `SqlQueryService` in `CodeMemory.Mcp`
- SELECT-only enforcement prevents accidental writes
- Parameterized queries to prevent SQL injection

### Acceptance criteria

- `SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10` returns results from the database
- `SELECT * FROM RelationshipRecord WHERE RelationshipType = 'Calls'` returns results
- `SELECT * FROM ChunkRecord ...` returns a clear error message
- `DROP TABLE SymbolRecord` returns an error (DDL rejected)
- Tool appears in MCP tool list when connected to AspNet host
- `dotnet build` passes

### Files likely involved

- `src/CodeMemory.AspNet/Tools/AspNetSqlQueryTool.cs` (new)
- `src/CodeMemory.AspNet/Program.cs`

---

## Task 6: Update `IndexingState` for per-repo completion across both stores

### Priority

Medium

### Goal

Ensure `IndexingState` correctly marks a repo as completed only after both the VectorStore collection and the DbContext tables are initialized.

### Why this exists

Currently `IndexingState.IsCompleted()` checks a single boolean per repo. With hybrid storage, `InitializeAsync` on `HybridStorageService` must succeed for both backends before the repo is considered ready. The `ping` tool must not return `indexingCompleted: true` until both stores are ready.

### Scope

- Review `HybridStorageService.InitializeAsync` — it already calls both `VectorStoreCollection.EnsureCollectionExistsAsync` and `DbContext.Database.EnsureCreated()`. Ensure error handling covers partial failures (VectorStore OK, DbContext fails → not completed).
- Update `IndexingEngine.RunIndexingAsync` — currently calls `await storage.InitializeAsync(ct)` and proceeds. Ensure the completion signal (`IndexingState.MarkCompleted`) fires only after both stores have data, not just after InitializeAsync.
- (This may already work if `MarkCompleted` is called after all store operations succeed. Verify the flow.)

### Constraints

- Must not change the `IndexingState` API (other agents depend on `ping` tool behavior)
- Must not affect the Mcp path

### Acceptance criteria

- When `HybridStorageService.InitializeAsync` fails on the DbContext but succeeds on VectorStore, the repo is NOT marked completed
- The `ping` tool returns `indexingCompleted: false` until both stores are ready
- Normal indexing flow completes with both stores ready, and `ping` returns `indexingCompleted: true`

### Files likely involved

- `src/CodeMemory.AspNet/Storage/HybridStorageService.cs`
- `src/CodeMemory/Services/IndexingEngine.cs`
- `src/CodeMemory/IndexingState.cs`

---

## Suggested Agent Handout Batches

### Batch A: core implementation (Tasks 2 + 3)

These have a clean dependency chain — entities before service. Can be handed to one agent.

### Batch B: integration (Tasks 4)

Depends on Tasks 2+3 being merged. Wires everything together.

### Batch C: tools + correctness (Tasks 5 + 6)

Independent of each other. Both depend on Task 4 being merged.

---

## Final Checklist

- [ ] every task has a clear owner-sized scope
- [ ] every task has acceptance criteria
- [ ] decision-gate tasks are clearly marked
- [ ] likely files are listed to reduce agent search time
- [ ] execution order reflects real dependencies
