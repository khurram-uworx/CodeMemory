# CodeMemory — AGENTS.md

## Purpose

Engineering constraints and implementation guidance for AI coding agents contributing to CodeMemory.

**First read [README.md](README.md)** for the project overview, quick start, and tool reference. Then read [ARCHITECTURE.md](ARCHITECTURE.md) for system architecture, data flow, dependency layering, storage schema, and current limitations.

---

## Domain Boundaries

Do not reinvent infrastructure. Prefer existing .NET and ecosystem primitives over custom solutions.

Forbidden: custom LLM clients, custom embedding pipelines (use `IEmbeddingGenerator` — reference implementations in Memori NuGet and `CodeMemory.AspNet.Extensions`), custom DI, custom vector DBs, custom chat orchestration.

CodeMemory is NOT: an IDE, a chat assistant, a code generator, or a standalone AI agent runtime. It IS: a repository intelligence and memory substrate exposed via MCP.

---

## Feature Development

- **MCP-First** — Every feature MUST be exposed as a deterministic, composable MCP tool returning structured JSON. No freeform prompting or unstructured text blobs inside tool logic.
- **Defaults** — `Microsoft.Extensions.AI`, `VectorData` abstractions, MCP exposure, Memori for embedding implementation
- **Reuse** — Prefer .NET and ecosystem primitives over custom solutions; prefer composition over new frameworks
- **Extensibility** — New features MUST extend the MCP tool surface, reuse existing abstractions, and not introduce parallel frameworks
- **Document** — Record reasoning for non-standard decisions

---

## Long-Term Vision

> A persistent, queryable, semantic memory layer for software systems.
Anything that does not improve repository cognition is out of scope.

---

## Task Format

Use `docs/TASKS-TEMPLATE.md` for new task breakdowns. Each task must include: Priority, Goal, Scope, Acceptance Criteria, Files Likely Involved.

---

## Project Structure

See ARCHITECTURE.md §Project Structure. Key rules:
- `BackgroundService` MUST live in `CodeMemory.AspNet`. Core indexing logic (`IndexingEngine`) lives in `CodeMemory` library.
- MCP tool types live in `CodeMemory.Mcp` namespace (core tools) and `CodeMemory.AspNet.Tools` (AspNet-specific)
- `IStorageService` interface and storage models live in `CodeMemory` assembly

### Multi-Repo Architecture

See ARCHITECTURE.md §Multi-Repo Architecture. Key constraints:
- `IStorageService` is the **only** per-repo concern — all other services stay non-keyed singletons
- `Stateless = true` (Streamable HTTP) — no session affinity
- `PerSessionExecutionContext = true` preserves `AsyncLocal` flow to tool handlers

## Non-Blocking Indexing & Ping Contract

Both hosts index in the background:
- **STDIO MCP** (`CodeMemory.Mcp`): `Task.Run` starts indexing before `host.RunAsync()`. MCP server accepts requests immediately.
- **ASP.NET** (`CodeMemory.AspNet`): `IndexingHostedService` (BackgroundService) initializes storage upfront, then indexes each repo sequentially. MCP endpoints are live immediately.

The `ping` MCP tool returns:
```json
{"status":"ok","indexingCompleted":true}
```
(or with `"fileWatcherActive":true` when the Mcp host watcher is running)
or when still indexing:
```json
{"status":"ok","indexingCompleted":false,"message":"Indexing in progress. Retry tools in a few seconds."}
```

**Agents must poll `ping` until `indexingCompleted` is `true` before calling other tools.** Failure to do so will return empty/partial results.

## Common Pitfalls

- SSE transport (`EnableLegacySse = true`) throws at startup when combined with `Stateless = true` — modern agents use Streamable HTTP, no SSE needed.
- Stateful mode (`Stateless = false`) breaks WebApplicationFactory tests — they don't send the MCP `initialize` handshake.
- Hardcoded `/api/mcp/default` in tests causes 404 after removing the fallback default repo — always route to a specific configured repo.
- Repo-relative paths resolve from `Environment.CurrentDirectory`, which differs between dev (AspNet project dir) and test (test bin dir) — use `Path.GetFullPath` with assembly-relative roots in test infrastructure.
- MCP SDK documentation lives in the NuGet cache, not on NuGet.org — NuGet.org search returns Azure Functions MCP docs for the legacy SDK, not the ASP.NET Core `ModelContextProtocol.AspNetCore` package.
- **In-memory storage (`Storage:Provider: "inmemory"`)** loses all data on restart — do not use for production persistence. SQLite (`"sqlite"`) persists vectors in `.codememory/sqlvec.db`. For production persistence, use `"pgvector"` or `"sqlserver"` providers with `HybridStorageService`.
- **Ping before use** — indexing is non-blocking in both hosts; agents MUST poll `ping` until `indexingCompleted: true` (see [Non-Blocking Indexing](#non-blocking-indexing--ping-contract) above).
- The `IndexingState` static class uses `ConcurrentDictionary` — it is process-scoped. In multi-repo ASP.NET, `IndexingState.IsCompleted()` without a repo name checks all repos are done.
- **`sql_query` MCP tool** requires `InMemoryVectorStore` — `"sqlite"`/`"pgvector"`/`"sqlserver"` returns an error. Full syntax reference in the tool's `[Description]`, discoverable via `tools/list`.

## Embedding Limitations & Agent Expectations

code-memory MCP configured for this repo defaults to the `NgramEmbeddingGenerator` (see [ADR Library-Embeddings-01](docs/adr/Library-Embeddings-01.md)) — a **deterministic character n-gram embedding** that requires no ML model, no API keys, and zero startup cost. It is consistent across processes and sessions. However, it is **not true semantic search**.

> AspNet host supports alternative embedding backends (`"onnx"`, `"ollama"`) that provide true semantic search — see `appsettings.json:Embedding:Provider` and [`CodeMemory.AspNet.Extensions`](ARCHITECTURE.md#embedding-providers).

**Agents using `semantic_search` or `sql_query` with `ORDER BY SIMILARITY` / `VECTOR_SEARCH` must follow these rules:**

- **Use literal terms** — query with words that appear in the codebase (identifiers, type names, keywords, comments). Synonyms will not match: `"delete"` and `"remove"` share zero n-gram overlap.
- **Set lower similarity thresholds** — use `minimumSimilarity: 0.3` to `0.5` rather than the ML-embedding default of `0.7`. The n-gram approach produces softer score distributions.
- **Prefer longer query strings** — queries shorter than 2 characters return a zero vector (no results). Queries of 5–15 characters work best.
- **Do not expect concept matching** — `"find slow code"` will not surface `PerformantQuery` or complexity-related comments. Construct queries from observed code terminology instead.

## Error Handling

MCP tools use three patterns — follow the one matching your return type:

| Return Type | Error Pattern | Examples |
|---|---|---|
| `string` | `try/catch` → `JsonSerializer.Serialize(new { status="error", message=ex.Message })` | `AdminTool`, `McpTools.Ping` |
| Typed record | Null-service guard → []; sentinel with `Warning` field; exceptions propagate to SDK | All `CodeMemory` library tools |
| `IDictionary<string, object?>` | `fail()` helper → `{ success: false, error: msg }` | `AspNetSqlQueryTool` |

- Log failures (`logger.LogWarning` for degraded paths, `logger.LogError` for exceptions) — do not rely on exceptions as the communication channel.
- Tools with external service dependencies use `GetService<T>` fallback — gracefully degrade when backing services are unavailable.
- MCP SDK serializes typed record returns; unexpected exceptions become JSON-RPC errors automatically.

## Testing

- **Framework:** NUnit 4.x — `[Test]`, `Assert.That(...)`, `Assert.ThrowsAsync`, no `[TestCase]`
- **Mocking:** Hand-written stubs in `MockServices.cs` — no mocking library dependency
- **Naming:** `Method_Scenario_ExpectedBehavior` PascalCase
- **Pattern:** Arrange-Act-Aggregate (AAA, no explicit comments needed)
- **Organization:** Mirror `src/` layout; one class per file, `*Tests.cs` suffix
- **Base classes:** `BaseToolTests` (MCP integration), `BaseServicesTests` (service tests with real SQLite)
- **Shared:** `MockServices.cs`, `TestLogger<T>`, `TestConstants`, `TestRepoHelper`, `fixtures/`

## Code Style

`.editorconfig` at repo root is authoritative. Key conventions not covered there:

- **Primary constructors:** Preferred for service/DI classes over classic constructor with `this.` field assignment
- **`sealed class`:** Default for non-abstract classes
- **`readonly` fields:** All DI-injected services
- **Collection expressions:** `[]` for empty/static, `new List<T>()` for mutable
- **Private fields:** No underscore prefix (`logger` not `_logger`)

## DI Conventions

- **Prefer `AddSingleton`** for all services. `IndexingEngine` is the sole `AddScoped` exception (AspNet only, per-repo scoping).
- **Register in `Program.cs`** — no auto-scanning, no shared DI modules.
- **MCP tools:** `[McpServerToolType]` class → discovered via `WithToolsFromAssembly(...)`. Tool instances resolved through DI (constructor injection of singletons).
- **Per-repo storage:** Bypasses DI — registered at runtime via `IServiceRegistry.Register(name, storage)` (thread-safe `ConcurrentDictionary` inside `ServiceRegistry`).
- **Storage providers:** use `IServiceCollection` extension methods (`AddCodeMemoryInMemoryStorage`, etc.) or factory methods (`CreateInMemoryStorage`).

