# ADR-Web-Data-01: Keep RepoRegistryDbContext and CodeMemoryDbContext Separate

---

## Context

The `CodeMemory.AspNet` project defines two EF Core `DbContext` classes:

| DbContext | Namespace | Role |
|---|---|---|
| `RepoRegistryDbContext` | `Registry/` | Control plane — tracks registered repos, their clone/index status, and discovered components |
| `CodeMemoryDbContext` | `Storage/` | Data plane — stores indexed symbols and relationships extracted from source code |

These two contexts interact with several shared services (`HybridStorageService`, `ServiceCollectionExtensions`, `Program.cs`) but serve different purposes.

### Key differences

| Aspect | `RepoRegistryDbContext` | `CodeMemoryDbContext` |
|---|---|---|
| **DbSets** | `Repositories`, `Components` | `Symbols`, `Relationships` |
| **Schema strategy** | Default schema (shared across all repos) | Per-repo dynamic schema via `SchemaModelCacheKeyFactory` |
| **Registration** | Standard DI: `AddDbContextFactory<T>()` | Per-repo `Func<CodeMemoryDbContext>` factories — **not** in DI |
| **Used with in-memory provider** | Yes | No — only used with relational providers (sqlite/pgvector/sqlserver) |
| **Entity ownership** | `RepositoryId` → `Components` (FK cascade) | Symbols ↔ Relationships (FK references) |

A proposal was raised to merge them into a single `DbContext` to reduce class count and simplify the DI surface.

---

## Decision

**Keep the two DbContexts separate.** Do not merge `RepoRegistryDbContext` and `CodeMemoryDbContext`.

---

## Rationale

### 1. Schema strategy conflict (fatal)

`CodeMemoryDbContext` uses per-repo schemas via `HasDefaultSchema(schema)` and a custom `SchemaModelCacheKeyFactory` to create unique EF Core model caches per repo. This is essential for multi-tenant isolation in shared databases (PostgreSQL, SQL Server).

`RepoRegistryDbContext` tables (`Repositories`, `Components`) are shared state — they do not belong in any single repo's schema. Merging would require:
- Putting registry tables inside a per-repo schema (semantically wrong), or
- Adding conditional schema logic in `OnModelCreating` (fragile and harder to maintain).

### 2. Registration lifecycle mismatch

- `RepoRegistryDbContext` is a singleton factory in DI (`IDbContextFactory<T>`).
- `CodeMemoryDbContext` is constructed per-repo with a schema parameter; it is never in DI.

Merging would force a single construction path, requiring the registry tables to either abandon DI or force the data plane into DI with a shared schema — breaking the per-repo isolation design.

### 3. Control plane vs data plane separation

These are logically distinct concerns:
- **Control plane** (registry): repo lifecycle management, clone/index status, component inventory. Changes at ops-time (add/remove repos, monitor status).
- **Data plane** (symbols): indexing output — read by MCP tools, written during indexing. Changes at index-time.

A broken migration or outage in one should not block the other. Merging couples their schema migrations, failure modes, and change cadences.

### 4. In-memory provider path

With `Storage:Provider: "inmemory"`, `CodeMemoryDbContext` is never instantiated — all data lives in `InMemoryVectorStore`. But `RepoRegistryDbContext` is still used for repo management. Merging would force the in-memory path to either:
- Register and configure an EF Core context it never needs, or
- Add conditional registration logic bypassing the merged context.

### 5. Migration entanglement

Registry schema changes (e.g., adding a `Tags` column to `Repositories`) and data plane changes (e.g., new indexes on `relationships`) evolve at different rates by different drivers. A single migration history would force them to be versioned and deployed together.

### 6. Minimal benefit

The claimed benefit — fewer classes — is negligible. The two contexts together are ~150 lines. The real complexity is in their separate registration paths and schema strategies, which merging would not simplify (it would complicate them with conditionals).

---

## Consequences

- **Positive:** Clear separation of concerns; independent schema evolution; no conditional logic in `OnModelCreating`; in-memory provider path stays clean; control plane remains available even if data plane migrations fail.
- **Negative:** Two DbContexts to maintain; `HybridStorageService` must manage two factory references (`Func<CodeMemoryDbContext>` + `IDbContextFactory<RepoRegistryDbContext>`); two migration histories to track.
- **Trade-off accepted:** The minor overhead of managing two contexts is outweighed by the architectural clarity and independence gained.

---

## Compliance

- All new tables/entities must be placed in the appropriate DbContext based on their role (control plane → `RepoRegistryDbContext`, data plane → `CodeMemoryDbContext`).
- Any service that needs both must inject both factories explicitly — no merging of concerns through a single context.
- A future ADR may revisit if the number of shared operations between the two contexts grows significantly.

---

## Alternatives considered

| Alternative | Rejected because |
|---|---|
| **Merge into one DbContext** | Schema strategy conflict, lifecycle mismatch, coupled migrations |
| **Partial merge (table-per-hierarchy)** | Would only mask the problem; underlying schema and lifecycle differences remain |
| **Separate databases entirely** | Over-engineered for current scale; adds connection management complexity without clear benefit |
