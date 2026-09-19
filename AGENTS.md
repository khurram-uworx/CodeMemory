# CodeMemory — AGENTS.md

## Purpose

Engineering constraints and implementation guidance for AI coding agents contributing to CodeMemory.

**First read [README.md](README.md)** for the project overview, quick start, and tool reference. Then read [ARCHITECTURE.md](ARCHITECTURE.md) for system architecture, data flow, dependency layering, storage schema, and current limitations.

---

## GitHub

- **Repo:** `khurram-uworx/CodeMemory`
- All `gh` commands require `--repo khurram-uworx/CodeMemory`
- **Write issue/PR bodies and commit messages to files** — never inline `--body "..."`/`-m "..."`.
  PowerShell (`pwsh`) parses quotes, `$`, backticks, and `(`/`)` inside double-quoted strings and
  mangles multi-line bodies; write the text to a temp file (e.g.
  `%TEMP%\opencode-<name>.md`) and pass it with `gh issue create
  --body-file <file>` / `gh pr create --body-file <file>` / `git commit -F <file>`.

### MCP Server Verification — Triage Bugs via GitHub Issues

A local code-memory MCP server is wired into the agent harness (`opencode.json`: `code-memory` →
`dotnet run --project .\src\CodeMemory.Mcp\CodeMemory.Mcp.csproj`, i.e. the current working tree).
When an MCP tool misbehaves — wrong results, cryptic errors, hangs, silent `[]` — treat it as a
repo defect to triage, not something to work around in the task at hand:

1. **Ping first** — `ping` returns indexing status and `version` (embeds the git hash); confirm
   the report is against the current tree, not a stale published package.
2. **Characterize** — rerun the failing call sequentially and concurrently. Corruption only in
   parallel is a known hazard (`#127`: shared non-thread-safe `SqlQueryParser` singleton).
3. **Search opened issues** — `gh issue list --repo khurram-uworx/CodeMemory`; if a matching
   report exists, add the reproduction details as a comment instead of duplicating (precedent:
   `#127` findings added as a comment after live re-reproduction).
4. **Open a new issue when required** — `gh issue create --repo khurram-uworx/CodeMemory` with
   the exact tool call, expected vs actual, ping version hash, and repro steps; record the number
   in the plan's issues log (see Task Format). Precedent: `#128` (silent `{}` rows) filed at
   discovery and fixed on the same branch that shipped `#120`.

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

### ADR Style

ADRs (Architecture Decision Records) should describe **what** the system does at an architectural level and **why**, without pinning down implementation method names, parameter types, or class signatures that can drift during implementation. Include intent, constraints, and tradeoffs; leave concrete API surface to the code. If an ADR contradicts the implementation, update the ADR toward the abstract intent — the implementation is the source of truth for specifics.

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

See ARCHITECTURE.md §Multi-Repo Architecture (AspNet). Key constraints:
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

## Repository Configuration (`.codememory.json`)

Per-repo configuration loaded by `IndexingEngine.RunIndexingAsync()` at index time. Place at repo root.

| Field | Type | Default | Description |
|---|---|---|---|
| `exclude` | `string[]` | `[]` | Additional file/directory patterns to skip beyond `.gitignore` and built-in ignores |
| `languageOverrides` | `object` | `{}` | Map of file extension → language name (e.g., `{".myext": "C#"}`) |
| `clusteringThreshold` | `double?` | `null` | Default threshold for component clustering; `null` means service falls back to 0.3 |

Files excluded by default (hardcoded in `FileCrawler.AlwaysIgnored`): `.git`, `.codememory`, `.memori`, `.codememory.json`, `node_modules`.

### Init Tool

`code-memory --init` (or `InitTool` MCP tool) creates `.codememory.json` with all defaults and adds `.codememory.json` to `.gitignore` (smart coverage detection via `GitIgnoreParser`).

## Common Pitfalls

- SSE transport (`EnableLegacySse = true`) throws at startup when combined with `Stateless = true` — modern agents use Streamable HTTP, no SSE needed.
- Stateful mode (`Stateless = false`) breaks WebApplicationFactory tests — they don't send the MCP `initialize` handshake.
- MCP SDK documentation lives in the NuGet cache, not on NuGet.org — NuGet.org search returns Azure Functions MCP docs for the legacy SDK, not the ASP.NET Core `ModelContextProtocol.AspNetCore` package.
- **Ping before use** — indexing is non-blocking in both hosts; agents MUST poll `ping` until `indexingCompleted: true` (see [Non-Blocking Indexing](#non-blocking-indexing--ping-contract) above).
- **`SqlQueryParser` is not thread-safe** — it wraps sqlparsercs's stateful `Parser` (mutable token/index instance state reused across `ParseSql` calls). Never hold a shared `SqlQueryParser` instance across concurrent calls; create one per parse (see #127).

## SQL Parser Development Reference

The SQL layer uses the `sqlparsercs` NuGet package (C# port of sqlparser-rs) via `SqlParser`,
`SqlParser.Ast`, `SqlParser.Dialects`. The upstream source is cloned locally at
`E:\github\SqlParser-cs` for investigating parser/tokenizer behavior and error messages.

`sql_query` validates identifiers against the table schema **before execution** (schema-first
diagnostics, see #122): `CodeMemory.Mcp.SqlQuery.SqlQueryValidator` checks `SELECT` projections,
`WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, and `JOIN ON` against `TableSchemaProvider` schemas
(and CTE/derived-table row keys), so an unknown column fails fast with
`Unknown column 'X'. Available columns on 'T': …` (plus `DESCRIBE`/`PRAGMA table_info` tips for
real tables) instead of silently dropping rows. Multi-table queries require table-qualified
columns — unqualified identifiers are rejected with alias guidance.

Known quirks (work around them in `CodeMemory.Mcp`/`CodeMemory.AspNet`, do not patch the library):
- Message duplication — `Parser.cs:6886` calls `Expected("Expected an expression, …")` while
  `Expected()` itself prefixes `"Expected "`, producing `Expected Expected …`; tokens are dumped in
  Rust-style debug form (`Identifier { Ident = FROM }`).
- `ParserException` / `TokenizeException` expose 1-based `Line` and `Column` properties — use them
  to produce parse errors with query-position context (snippet + caret) rather than raw message dumps.
  Some structural errors (e.g. a trailing comma before `FROM`) report `Line`/`Column == 0`;
  `ParseErrorFormatter` then locates the offending token textually, and appends a stray-`;` removal
  tip for "Expected a SQL statement, found X" errors.

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

## Testing Conventions

- **Framework:** NUnit 4.x — `[Test]`, `Assert.That(...)`, `Assert.ThrowsAsync`, no `[TestCase]`
- **Mocking:** NSubstitute-based factory methods in `MockServices.cs` — use `MockServices.CreateXxxService()` in tests. For unique test scenarios, configure inline with `Substitute.For<IService>()` instead of extending the shared factory.
- **Naming:** `Method_Scenario_ExpectedBehavior` PascalCase
- **Pattern:** Arrange-Act-Assert (AAA); no explicit comments needed
- **Organization:** Mirror `src/` layout; one class per file, `*Tests.cs` suffix
- **Base classes:** `BaseToolTests` (MCP integration), `BaseServicesTests` (service tests with real SQLite)
- **Shared:** `MockServices.cs`, `TestLogger<T>`, `TestConstants`, `TestRepoHelper`, `fixtures/`

## Code Style

- `.editorconfig` at repo root is authoritative; follow it over any convention below.
- **Private fields:** `camelCase` without `_` prefix (`logger`, not `_logger`).
- **Member ordering:** inner classes → constructors → properties → methods; static before instance; private → protected → internal → public.
- **Primary constructors:** preferred for service/DI classes over classic constructor with field assignment.
- **Sealed by default:** use `sealed class` for non-abstract classes unless inheritance is explicitly designed.
- **Collection expressions:** `[]` for empty/static collections; `new List<T>()` or `new Dictionary<K,V>()` for mutable ones.
- **Nullable reference types:** enabled; do not introduce avoidable warnings.
- **Omit braces** from single-line `if`/`else` bodies when the body fits one line and is on the same line as the condition.
- **No comments** in generated code unless explaining a non-obvious design decision.
- **No Hungarian notation** — no prefixes encoding scope or mutability.
- **`readonly` fields:** All DI-injected services.
- **MCP tool return types:** Always use typed records/classes, never `string`. The MCP SDK serializes typed returns automatically into the JSON-RPC envelope. Manual `JsonSerializer.Serialize` + `string` return bypasses SDK serialization, swallows exceptions into success responses instead of proper JSON-RPC errors, and hides the response schema from `tools/list`. Define result types in the tool file (like `AspNetSqlQueryResult`) or under `Mcp/Models/` for shared types.

## DI Conventions

- **Prefer `AddSingleton`** for all services. `IndexingEngine` is the sole `AddScoped` exception (AspNet only, per-repo scoping).
- **Register in `Program.cs`** — no auto-scanning, no shared DI modules.
- **MCP tools:** `[McpServerToolType]` class → discovered via `WithToolsFromAssembly(...)`. Tool instances resolved through DI (constructor injection of singletons).
- **Per-repo storage:** Bypasses DI — registered at runtime via `IServiceRegistry.Register(name, storage)` (thread-safe `ConcurrentDictionary` inside `ServiceRegistry`).
- **Storage providers:** use `IServiceCollection` extension methods (`AddCodeMemoryInMemoryStorage`, etc.) or factory methods (`CreateInMemoryStorage`).

