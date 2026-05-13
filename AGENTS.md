# CodeMemory — AGENTS.md

## Purpose

Engineering constraints and implementation guidance for AI coding agents contributing to CodeMemory.

**First read [README.md](README.md)** for the project overview, quick start, and tool reference. Then read [ARCHITECTURE.md](ARCHITECTURE.md) for system architecture, data flow, dependency layering, storage schema, and current limitations.

---

## Core Principle

Do not reinvent infrastructure. Prefer existing .NET and ecosystem primitives over custom solutions.

Forbidden: custom LLM clients, custom embedding pipelines (use `IEmbeddingGenerator` — reference implementation in Memori NuGet), custom DI, custom vector DBs, custom chat orchestration.

Memori was adopted for both embedding generation (`NgramEmbeddingGenerator`) and in-memory vector storage (`InMemoryVectorStore`) to provide a **zero-dependency out-of-box experience** — no API keys, no model downloads, no database native binaries required for the default configuration.

---

## MCP-First Design

MCP is the **only** external interface. Every feature MUST be exposed as an MCP tool.

Tool rules:
- deterministic where possible, return structured JSON
- no freeform prompting or unstructured text blobs inside tool logic
- composable (tools should work independently and together)

---

## AI Agent Behavior

- Prefer composition over new frameworks
- Reuse .NET primitives first
- Document reasoning for non-standard decisions
- Default to: `Microsoft.Extensions.AI`, `VectorData` abstractions, MCP exposure, Memori for embedding implementation

---

## Extensibility

New features MUST:
- extend the MCP tool surface
- reuse existing abstractions
- not introduce parallel frameworks

---

## Non-Goals

CodeMemory is NOT: an IDE, a chat assistant, a code generator, or a standalone AI agent runtime.

It IS: a repository intelligence and memory substrate exposed via MCP. Includes dependency graphs, architecture overviews, component clustering, and git history — all accessible through MCP tools.

---

## Long-Term Vision

All contributions must reinforce:

> A persistent, queryable, semantic memory layer for software systems.

Anything that does not improve repository cognition is out of scope.

---

## Task Format

Use `docs/TASKS-TEMPLATE.md` for new task breakdowns. Each task must include: Priority, Goal, Scope, Acceptance Criteria, Files Likely Involved.

---

## Project Structure

Four projects:
- **`CodeMemory`** — Pure library (`Microsoft.NET.Sdk`, `OutputType Library`). No ASP.NET dependency. Contains all service logic, MCP tool definitions, storage interfaces, and models. Includes `IndexingState` static class tracking per-repo indexing completion.
- **`CodeMemory.Storage`** — Vector store providers (SQLite + In-memory). References `CodeMemory` for interfaces and model types. Depends on `Memori` NuGet for `InMemoryVectorStore`.
- **`CodeMemory.Mcp`** — Standalone stdio MCP server for single-repo agent usage. Uses `WithStdioServerTransport`. Takes optional `--repo <path>` argument. Indexing is **non-blocking** — starts in background `Task.Run`, server loop starts immediately.
- **`CodeMemory.AspNet`** — ASP.NET Core host. Owns `Program.cs`, DI registration, MCP Streamable HTTP transport, `BackgroundService` lifecycle (`IndexingHostedService`).

### Key rules

- Services with `BackgroundService` inheritance MUST live in `CodeMemory.AspNet`. Core indexing logic (`IndexingEngine`) lives in `CodeMemory` and is wrapped by `IndexingHostedService` in `CodeMemory.AspNet`.
- MCP tool types live in `CodeMemory` (`CodeMemory.Mcp` namespace). Registration uses `WithToolsFromAssembly(typeof(McpTools).Assembly)` from both `CodeMemory.AspNet.Program.cs` and `CodeMemory.Mcp.Program.cs`.
- `IStorageService` interface and storage models (`SymbolRecord`, `ChunkRecord`, etc.) live in `CodeMemory.Storage.Services` / `CodeMemory.Storage.Models` namespaces but in the `CodeMemory` assembly.

### Multi-Repo Architecture

Multi-repo support uses **`StorageServiceRouter` + `IRepoContextAccessor` + per-repo MCP endpoints** — no keyed DI, no middleware, no `RequestServices` swap.

- **`IServiceRegistry`** / **`ServiceRegistry`** (`CodeMemory.AspNet/Configuration/`) — thread-safe registry of per-repo `IStorageService` instances keyed by repo name. Generalization of the removed `IStorageServiceRegistry` to support future service types beyond storage.
- **`StorageServiceRouter`** (`CodeMemory.AspNet/Configuration/`) — implements `IStorageService`, delegates to per-repo storage based on `IRepoContextAccessor.CurrentRepoName`. All 15 methods forward with `GetStorage()`.
- **`IRepoContextAccessor`** / **`RepoContextAccessor`** (`CodeMemory.AspNet/Configuration/`) — `AsyncLocal<string?>` ambient context, singleton-safe, no scoped DI needed.
- **`ConfigureSessionOptions`** — MCP SDK callback in `Program.cs` that extracts repo name from path `/api/mcp/{repoName}` and sets `IRepoContextAccessor.CurrentRepoName`.
- **`IStorageService` is the only per-repo concern** — all other services (`DependencyGraphService`, `ArchitectureService`, etc.) stay non-keyed singletons resolving from `StorageServiceRouter`.
- `Stateless = true` (Streamable HTTP) — no session affinity, no SSE, each request self-contained.
- `PerSessionExecutionContext = true` preserves `AsyncLocal` flow to tool handlers.

## Non-Blocking Indexing & Ping Contract

Both hosts index in the background:
- **STDIO MCP** (`CodeMemory.Mcp`): `Task.Run` starts indexing before `host.RunAsync()`. MCP server accepts requests immediately.
- **ASP.NET** (`CodeMemory.AspNet`): `IndexingHostedService` (BackgroundService) initializes storage upfront, then indexes each repo sequentially. MCP endpoints are live immediately.

The `ping` MCP tool returns:
```json
{"status":"ok","indexingCompleted":true}
```
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
- **In-memory storage (`Storage:Provider: "inmemory"`)** loses all data on restart — do not use for production persistence. SQLite (`"sqlite"`) persists vectors in `.memorycode/sqlvec.db`.
- **Ping before use** — indexing is non-blocking in both hosts; agents MUST poll `ping` until `indexingCompleted: true` (see [Non-Blocking Indexing](#non-blocking-indexing--ping-contract) above).
- The `IndexingState` static class uses `ConcurrentDictionary` — it is process-scoped. In multi-repo ASP.NET, `IndexingState.IsCompleted()` without a repo name checks all repos are done.

## Summary

When uncertain: use `Microsoft.Extensions.AI` abstractions, expose via MCP, avoid reinventing infrastructure, and prioritize repository understanding over feature expansion. Architecture intelligence services (`DependencyGraphService`, `ArchitectureService`, `ComponentClusteringService`, `GitHistoryService`) follow the same patterns — compose existing abstractions, register in `CodeMemory.AspNet/Program.cs`, expose via MCP tools. Embedding implementations come from the `Memori` NuGet package.
