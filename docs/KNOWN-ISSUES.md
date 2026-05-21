# CodeMemory.AspNet — Enterprise Gaps

Documented gaps for enterprise / central-deployment readiness of the ASP.NET Core host (`CodeMemory.AspNet`). The STDIO host (`CodeMemory.Mcp`) is already well-suited for single-repo agent use. These are the gaps that need closing before AspNet is production-ready at scale.

---

## 3. Custom PgVectorStore Duplicates Official SK Connector

`src/CodeMemory.AspNet/Storage/PgVector/` contains a full custom `VectorStore` implementation (`PgVectorStore.cs` + `PgVectorCollection.cs` + `PgVectorOptions.cs`). This is ~700 lines of custom PostgreSQL+pgvector integration code.

The project's `.csproj` already references `Microsoft.SemanticKernel.Connectors.PgVector` (the official connector). The custom implementation was written before the official SK connector was available.

**Risk:** The custom implementation may have subtle divergences from the official `Microsoft.Extensions.VectorData` abstraction contract. It's also dead-weight maintenance burden.

**What's needed:**
- Replace `PgVectorStore`/`PgVectorCollection` usage with `PostgresVectorStore` from the official SK connector
- Remove the `Storage/PgVector/` directory entirely
- Verify schema isolation (per-repo schemas) works with the official connector

---

## 4. No Authentication / Authorization

The AspNet host currently has zero auth. Any client that can reach the endpoint can query any repo. Enterprise deployments require:

- **API Key auth** (static key per deployment, simplest)
- **JWT Bearer auth** (Entra ID, Okta, Auth0 integration)
- **Per-repo access control** (which clients can access which repos)
- **Admin vs read-only roles** (index management vs query-only)

The CORS config shows intent for access control (`Cors:AllowedOrigins` in appsettings.json) but no actual auth middleware is wired up.

---

## 7. No Docker / Container Support

No Dockerfile, no docker-compose, no container-specific configuration:
- No multi-stage build targeting the AspNet project
- No environment-variable-first config for containerized deployments
- No graceful shutdown hooks beyond what ASP.NET provides by default
- No container health check configuration

---

## 8. SqlServer Provider Is Untested

The EF Core SQL Server provider is wired up in `ServiceCollectionExtensions.cs` (`createSqlServerStorage`) and the SqlServer VectorStore is referenced in the .csproj, but:

- Zero integration tests for the SqlServer provider
- The SqlServer VectorStore (`Microsoft.SemanticKernel.Connectors.SqlServer`) is in preview (`1.74.1-preview`) — compatibility with the VectorData abstraction verisons needs validation
- Schema isolation via `SchemaModelCacheKeyFactory` is untested with SQL Server

---

## 9. PgVector Tests Are [Explicit] and Lack CI

All PgVector tests (`PgVectorStorageTests.cs`, `PgVectorStorageServiceTests.cs`) are marked `[Explicit("PgVector required")]` with a hardcoded localhost connection string. They require a running PostgreSQL instance.

No CI pipeline integration — these tests never run in CI. No testcontainers or ephemeral PostgreSQL setup.

---

## 10. Configuration Is appsettings.json Only

Configuration is loaded exclusively from `appsettings.json` / `appsettings.Development.json`. Enterprise deployments typically need:

- Environment variable overrides (e.g. `Storage__Provider`, `ConnectionStrings__PgVector`)
- User secrets / Azure Key Vault for connection strings
- Command-line argument overrides
- Per-repo configuration that can be dynamically added at runtime without restart
- Config validation at startup (fail-fast for missing required connection strings)

---

## 11. CORS Is Permissive by Default

```csharp
if (corsOrigins is { Length: > 0 })
    policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
else
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
```

When `Cors:AllowedOrigins` is empty (the default in `appsettings.json`), `AllowAnyOrigin()` is used. For enterprise deployments this should default to deny-origin rather than allow-all.

---

## 12. No Rate Limiting / Request Size Limits

No rate limiting middleware. No request body size limits on the MCP HTTP endpoint. An attacker (or buggy client) could send large payloads or flood the endpoint.

---

## 16. Admin MCP Tools Are Entirely Commented Out

`src/CodeMemory/Mcp/AdminTool.cs` contains commented-out MCP tool methods:
- `RescanRepositoryAsync` — trigger re-index
- `GetRepositoryRoot` — get current repo path

These exist as commented code with a note about `IIndexingService` interface that was never created. Enterprise deployments need these tools to manage the index lifecycle without restarting the process.

---

## Summary Table

| # | Gap | Impact | Complexity |
|---|-----|--------|------------|
| 3 | Custom PgVectorStore cleanup | Medium — tech debt | Low |
| 7 | No Docker | Medium — deployment | Low |
| 8 | SqlServer untested | Low — incomplete | Low |
| 9 | PgVector tests Explicit | Low — CI gap | Low |
| 10 | Config rigidity | Medium — ops | Low |
| 11 | CORS permissive | Medium — security | Low |
| 12 | No rate limiting | Low — security | Low |
| 16 | Admin tools commented out | Low — missing feature | Low |


# Known Issues

---

## 1. `ComponentMapping` Static Cache Breaks Multi-Repo Component Resolution

**Status: Confirmed. Root cause identified, not yet fixed.**

`ComponentMapping` (`src/CodeMemory/Services/Architecture/ComponentMapping.cs`) is a **static** class with a `static ConcurrentDictionary` storing file path prefix → component name mappings. It is populated during indexing by `IndexingEngine.RunIndexingAsync()` calling `ComponentMapping.Initialize()`.

**Problem:** In a multi-repo ASP.NET deployment, `ComponentMapping` is shared across all repos. When any repo undergoes a full reindex, `Initialize()` calls `prefixToComponent.Clear()` and repopulates from only that repo's project files. This **destroys all other repos' component mappings**, causing `ArchitectureService` and `ComponentClusteringService` to fall back to directory-depth-based component names for those repos instead of the correct project-derived names.

**Affected:**
- `ArchitectureService.GetOverviewAsync()` — components misnamed for unaffected repos
- `ComponentClusteringService.GetClustersAsync()` — same
- All MCP tools that consume component names

**Root cause:**
- Static state (`ComponentMapping`) should either be per-repo (keyed by repo name) or `ComponentMapping.Initialize()` should merge rather than replace
- No existing mechanism to persist/restore component mappings per repo

**Potential fixes (not implemented):**
- Make `ComponentMapping` instance-based with repo-scoped lifetime (requires DI changes and a per-repo registry)
- Change `Initialize()` to accept a repo name key and store mappings in a `ConcurrentDictionary<string, ConcurrentDictionary<string, string>>` keyed by repo
- Persist component mappings in storage (add `ComponentMappingRecord` to storage schema)

---

## 2. MCP Resource Endpoints
**Status: Not started. No `McpServerResourceType` usage anywhere.**

The MCP spec supports `resources/` (readable data endpoints) in addition to `tools/`. Could expose:
- `codememory://architecture/overview` — structured architecture doc
- `codememory://hotspots` — hotspot ranking
- **Effort**: Low. Wrap existing services as `[McpServerResourceType]`.

---

## 3. Paginated / Memory-Bounded Storage Queries
**Status: Not started. `IStorageService` has `int top = 100` defaults on `GetSymbolsByKindAsync` and `GetSymbolsByFileAsync`, but `ArchitectureService.GetOverviewAsync` loads up to 100K symbols per kind. `ComponentClusteringService` queries relationships per symbol.**

For repos >100K symbols, pagination or streaming would prevent OOM.
- `IStorageService` currently has no count or paginated get methods
- **Effort**: Medium. Requires new `IStorageService` methods (count, paginated get) or streaming.

---

## 4. Edit Context Caching
**Status: Not started. `EditContextService` computes context fresh on every call. No `MemoryCache` or `IMemoryCache`.**

Contexts for the same symbol change only when the index changes. An in-memory LRU cache keyed by `(symbolPath, options hash)` would improve latency.
- **Effort**: Low. Add `MemoryCache` wrapper in `EditContextService`.

---

## 5. Uncomment/Implement `RescanRepositoryAsync` MCP Tool
**Status: Partially started. `AdminTool.cs` exists but has all tool methods commented out.**

There is an `AdminTool` with `[McpServerToolType]` that contains a fully drafted (but commented-out) `RescanRepositoryAsync` method and a `GetRepositoryRoot` method. The tool would let agents trigger a full re-index on demand. Currently:
- `IIndexingService` is commented out as a dependency
- No re-trigger mechanism exists (only startup indexing via `IndexingHostedService` or `Task.Run`)
- A working rescan tool would give agents a way to recover from stale indexes without restarting the server
- **Effort**: Low-Medium. Uncomment, add `IIndexingService` interface + implementation, wire up in DI.
