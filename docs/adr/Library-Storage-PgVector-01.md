# ADR-Storage-PgVector-Connector-01: Replace SK PgVector Connector with Custom Implementation

---

## Context

CodeMemory supports pgvector as a vector storage provider, alongside in-memory, SQLite, and SQL Server.

Originally there were **two** PgVector-related implementations:

| Implementation | Location | Status |
|---|---|---|
| SK `PostgresVectorStore` (from `Microsoft.SemanticKernel.Connectors.PgVector`) | `ServiceCollectionExtensions.CreatePgVectorStorage` | Used in production |
| Custom `PgVectorStore` / `PgVectorCollection` | `Storage/PgVector/` | Existing but unused |

The SK connector carried a transitive dependency on `Npgsql >= 8.0.7`. The project also referenced `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.1, which transitively resolved `Npgsql 10.0.2`.

At runtime, the SK connector called the parameterless `NpgsqlConnection.ReloadTypesAsync()` — an API that existed in Npgsql 8.x but was **removed** in Npgsql 10.x (the type-reloading functionality was consolidated into `NpgsqlDataSource.ReloadTypes()`). This caused:

```
System.MissingMethodException: Method not found:
  'System.Threading.Tasks.Task Npgsql.NpgsqlConnection.ReloadTypesAsync()'
```

Simple version pinning was not an option: downgrading Npgsql to 8.0.7 caused `CS1705` because `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.1 was compiled against Npgsql 10.0.2 and required the newer assembly version.

The upstream fix ([microsoft/semantic-kernel#13724](https://github.com/microsoft/semantic-kernel/pull/13724), merged 2026-03-31) updates the SK PgVector connector to target Npgsql 10.x, but has not shipped as a NuGet package as of this writing.

---

## Decision

**Remove `Microsoft.SemanticKernel.Connectors.PgVector` as a dependency and standardize on the custom `PgVectorStore/PgVectorCollection` implementation.**

The custom implementation required a one-line fix: `conn.ReloadTypes()` → `dataSource.ReloadTypes()` (the data-source-level API exists in Npgsql 10.x and achieves the same result — refreshing the type cache after `CREATE EXTENSION IF NOT EXISTS vector`).

---

## Rationale

### 1. Npgsql version conflict resolved at the root

The SK connector pinned us to Npgsql 8.x API surface while `Npgsql.EntityFrameworkCore.PostgreSQL` required 10.x. The custom implementation uses `NpgsqlDataSource.ReloadTypes()`, which is available in Npgsql 10.x, removing the conflict entirely.

### 2. Removes dual-implementation debt

Having both a custom and SK-based PgVector implementation for the same provider was a maintenance drag — changes to one had to be mirrored in the other, and it was unclear which was canonical. Standardizing on the custom implementation eliminates this.

### 3. Simpler dependency graph

Removing the SK connector package removes one transitive dependency chain and the NuGet version conflict that came with it. The project now depends directly on the `Pgvector` NuGet package (for `Vector` type) and uses Npgsql's ADO.NET APIs directly — no SK abstraction layer.

### 4. No loss of functionality

The custom `PgVectorCollection` supports all operations needed by `HybridStorageService`:

| Operation | Supported |
|---|---|
| `EnsureCollectionExistsAsync` (CREATE TABLE + vector extension + HNSW/IVFFLAT index) | Yes |
| `UpsertAsync` (single + batch) | Yes |
| `GetAsync` (by key, keys, filter) | Yes |
| `DeleteAsync` (by key, keys) | Yes |
| `SearchAsync` (cosine distance, HNSW/IVFFLAT) | Yes |
| `CollectionExistsAsync` | Yes |

---

## Consequences

### Positive

- Npgsql version conflict eliminated — both the vector store and EF Core share the same Npgsql 10.x runtime
- Reduced NuGet dependency count (removed `Microsoft.SemanticKernel.Connectors.PgVector`)
- Single canonical PgVector implementation to maintain
- Simpler code — direct ADO.NET rather than SK connector abstractions
- The custom implementation is already tested (`PgVectorStorageTests`, `PgVectorStorageServiceTests`)

### Negative

- Loses the SK connector's built-in `AddPostgresVectorStore` DI registration helpers (irrelevant — CodeMemory creates stores via `CreatePgVectorStorage` factory method)
- If a future version of the SK connector fully supports Npgsql 10.x and offers meaningful value (e.g., automatic schema migrations, advanced query optimizations), re-adopting it would require reverting this ADR

### Neutral

- The `Npgsql` and `Pgvector` NuGet packages remain as direct dependencies (they were already required by the custom implementation)

---

## Alternatives considered

| Alternative | Rejected because |
|---|---|
| **Pin Npgsql to 8.0.7** | CS1705: `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.1 compiled against Npgsql 10.0.2 — assembly version mismatch blocks compilation |
| **Downgrade EF Core PostgreSQL provider to 8.x** | Requires downgrading `Microsoft.EntityFrameworkCore` from 10.x to 8.x — too invasive |
| **Wait for upstream NuGet release** | Blocked on SK team's release cycle; the fix exists in source but has no published ETA |
| **Reflection-based workaround in HybridStorageService** | Brittle — would need to intercept SK connector internals at runtime |
| **Binding redirects / assembly redirection** | Fragile; doesn't solve the MissingMethodException (the method genuinely doesn't exist in 10.x) |
| **Keep both implementations** | Dual-maintenance cost with no benefit; the conflict resurfaces every time dependency versions change |
