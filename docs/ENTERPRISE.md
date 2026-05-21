# CodeMemory.AspNet — Enterprise Gaps

Documented gaps for enterprise / central-deployment readiness of the ASP.NET Core host (`CodeMemory.AspNet`). The STDIO host (`CodeMemory.Mcp`) is already well-suited for single-repo agent use. These are the gaps that need closing before AspNet is production-ready at scale.

---

## 1. Incremental Indexing (Full Re-Index on Every Startup)

Indexing is currently a full re-scan every time the process starts. For large repos or many repos this is a dealbreaker. The `AdminTool` class in `src/CodeMemory/Mcp/AdminTool.cs` has a `RescanRepositoryAsync` method entirely commented out — the infrastructure for index management was never completed.

**What's needed:**
- Differential file scanning (compare mtime/hash against last indexed state)
- Per-file upsert/delete instead of batch clear-all
- Persist indexing watermark per repo (last indexed commit hash or file mtimes)
- MCP tool to trigger re-index (`rescan_repository`)
- MCP tool to report index status per repo (`index_status`)

---

## 2. AspNetSqlQueryTool Is Crippled vs Mcp SqlQueryTool

The AspNet SQL tool (`src/CodeMemory.AspNet/Tools/AspNetSqlQueryTool.cs`) is severely limited compared to the Mcp version (`src/CodeMemory.Mcp/Tools/SqlQueryTool.cs` + `src/CodeMemory.Mcp/SqlQuery/`).

**AspNetSqlQueryTool limitations:**
- Only `SymbolRecord` and `RelationshipRecord` — explicitly rejects `ChunkRecord` ("use semantic_search instead")
- No JOIN support at all (validated and rejected early)
- No CTEs or derived tables
- No aggregate functions (COUNT, SUM, AVG, MIN, MAX)
- No GROUP BY / HAVING
- No vector search (`ORDER BY Similarity DESC`)
- No DISTINCT
- Uses EF Core raw SQL translation (logical name → physical table/column mapping)
- Single-statement only, no subqueries

**Mcp SqlQueryTool capabilities:**
- All three tables (SymbolRecord, ChunkRecord, RelationshipRecord)
- JOINs (INNER, LEFT, CROSS, self-joins)
- CTEs (non-recursive, chained)
- Derived tables (subqueries in FROM)
- Aggregates + GROUP BY + HAVING + DISTINCT
- Vector search via `ORDER BY Similarity DESC`
- Arithmetic expressions, string concatenation, parenthesized WHERE

These should converge into a single implementation that works for both backends (InMemoryVectorStore via LINQ, and relational stores via SQL passthrough).

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

## 5. No Health / Readiness Endpoints

No `/healthz` (liveness) or `/readyz` (readiness) endpoints for Kubernetes probes, load balancers, or container orchestrators.

The root `GET /` returns a human-readable status page which is not machine-parseable for health checks in standard formats. The `ping` MCP tool reports indexing state but is only accessible via JSON-RPC POST to `/api/mcp/{repo}` — not usable as a health probe.

---

## 6. No OpenTelemetry / Metrics

No structured observability:
- No request-level tracing across the indexing → search pipeline
- No metrics counters for tool invocations, query latency, indexing duration
- No metric exports (Prometheus, Application Insights, etc.)
- No structured logging beyond `ILogger<T>` (no log correlation IDs, no semantic event types)

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

## 13. No Dynamic Repository Registration

Repositories are configured statically in `appsettings.json:Repositories` and registered at startup in `Program.cs`. Adding a new repo requires a process restart.

Enterprise scenarios may need:
- Dynamic repo registration via API or config file watch
- Runtime repo de-registration
- Repository health status (is the path accessible? is indexing stuck?)

---

## 14. IndexingHostedService Lacks Monitoring

The background indexing service:
- Has no progress reporting (percent complete, files indexed, current file)
- Has no retry logic for transient failures (network issues during git analysis)
- If a single repo fails to index, it logs the error but continues — but there's no way for operators to know a repo is in a broken state
- No timeout/scoping for individual repo indexing (one hung repo blocks all subsequent repos)

---

## 15. No Graceful Degradation for Missing Embedding Generator

`Program.cs` always resolves `IEmbeddingGenerator<string, Embedding<float>>` from DI without a null check. While `NgramEmbeddingGenerator` is always registered, if someone removes it from DI (or replaces it with a provider that fails at startup), the entire application fails to start.

The core `StorageService` already supports `embeddingGenerator: null` (no chunk storage) — the AspNet startup should support this gracefully.

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
| 1 | Incremental indexing | High — scales to large/many repos | High |
| 2 | sql_query convergence | Medium — dual tool confusion | Medium |
| 3 | Custom PgVectorStore cleanup | Medium — tech debt | Low |
| 4 | No auth | High — security | Medium |
| 5 | No health endpoints | Medium — ops | Low |
| 6 | No OpenTelemetry | Medium — ops | Medium |
| 7 | No Docker | Medium — deployment | Low |
| 8 | SqlServer untested | Low — incomplete | Low |
| 9 | PgVector tests Explicit | Low — CI gap | Low |
| 10 | Config rigidity | Medium — ops | Low |
| 11 | CORS permissive | Medium — security | Low |
| 12 | No rate limiting | Low — security | Low |
| 13 | Static repo registration | Medium — ops | Medium |
| 14 | IndexingHostedService monitoring | Medium — ops | Medium |
| 15 | Missing embedding generator handling | Low — resilience | Low |
| 16 | Admin tools commented out | Low — missing feature | Low |
