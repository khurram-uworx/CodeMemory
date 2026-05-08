# CodeMemory — PLAN.md

## Purpose

This document defines the **execution plan for coding agents** building CodeMemory.

It is used alongside AGENTS.md (engineering rules and constraints).

PLAN.md translates intent into **structured execution strategy and task decomposition guidance**.

---

## 1. Execution Philosophy

CodeMemory is built using **incremental, verifiable system construction**.

Agents MUST:

* Build smallest useful vertical slices first
* Prefer working end-to-end systems over isolated components
* Validate each layer before adding complexity
* Avoid speculative architecture

---

## 1a. Project Structure

```
/CodeMemory
├── src/
│   ├── CodeMemory/              # ASP.NET Core host + Background Service
│   ├── CodeMemory.Storage/      # Vector store provider implementation (SQLite)
│   └── CodeMemory.Tests/        # NUnit test project
├── docs/
│   ├── PLAN.md
│   └── TASKS.md
├── AGENTS.md
└── .index/                       # Runtime index storage directory
```

### Dependency Layering

```
CodeMemory (host)
  ├── Microsoft.Extensions.AI.Abstractions     (IEmbeddingGenerator, IChatClient)
  ├── Microsoft.Extensions.VectorData.Abstractions
  └── CodeMemory.Storage
        └── Microsoft.SemanticKernel.Connectors.SqliteVec  (implements IVectorStore)
```

* `CodeMemory` — the core host project. References **only abstractions** from Microsoft.Extensions.AI and Microsoft.Extensions.VectorData. The user provides the actual embedding generator via DI configuration.
* `CodeMemory.Storage` — hosts the SQLite vector store connector. References the concrete `Microsoft.SemanticKernel.Connectors.SqliteVec` package. Can be swapped for another provider (pgvector, Qdrant, etc.) later.
* `CodeMemory.Tests` — NUnit tests.

The core library (`CodeMemory`) MUST NOT hardcode any concrete embedding or vector store implementation. All concrete dependencies are provided by `CodeMemory.Storage` and user DI configuration.

---

## 2. High-Level Build Phases

### Phase 0 — Foundation Setup (COMPLETED / NOT NEEDED)

Goal: Establish runnable .NET service baseline.

Architecture decisions:

* **Host**: ASP.NET Core app running on `http://localhost:8080`
* **Lifecycle**: Background Service (`IHostedService`) that indexes eagerly on startup — user controls lifecycle by starting/stopping the process
* **MCP Transport**: Streamable HTTP via `ModelContextProtocol.AspNetCore` (SSE is deprecated in newer MCP spec)
* **MCP Endpoint**: `/api/mcp` (or `/mcp`)

Deliverables:

* ASP.NET Core host on `localhost:8080`
* `src/CodeMemory/` — core host project (references only abstractions)
* `src/CodeMemory.Storage/` — SQLite vector store provider
* `src/CodeMemory.Tests/` — NUnit test project
* Dependency injection configured
* Logging + configuration setup

Success Criteria:

* Service runs locally on `localhost:8080`
* Health endpoint responds
* Background service starts indexing on launch

---

### Phase 1 — Repository Indexing Core

Goal: Convert source code into structured knowledge units.

Deliverables:

* File system crawler
* Language parser integration (Roslyn / Tree-sitter)
* Symbol extraction pipeline
* Basic chunking strategy (semantic units)

Success Criteria:

* Repo can be fully scanned
* Symbols extracted and stored

---

### Phase 2 — Knowledge Storage Layer

Goal: Persist repository intelligence.

Architecture decisions:

* **Vector store**: SQLite via `Microsoft.SemanticKernel.Connectors.SqliteVec` (implements `Microsoft.Extensions.VectorData.IVectorStore`)
* **Storage location**: `.index/` directory relative to the repo root (SQLite database file)
* **Embedding generation**: Via `IEmbeddingGenerator<TInput,TEmbedding>` from `Microsoft.Extensions.AI.Abstractions`. The embedding generator is **user-provided** (configured via DI) — CodeMemory itself only references the abstraction
* **Core dependency rule**: `CodeMemory` references ONLY `Microsoft.Extensions.VectorData.Abstractions` and `Microsoft.Extensions.AI.Abstractions`. Concrete implementations live in `CodeMemory.Storage` or are injected by the user

Deliverables:

* SQLite with vector extensions via `Microsoft.SemanticKernel.Connectors.SqliteVec`
* Storage schema for:
  * symbols
  * chunks
  * embeddings
  * relationships
* All vector operations via `IVectorStore` abstraction (no hardcoded SQLite logic in core)

Success Criteria:

* Data survives restart
* Retrieval works without re-indexing
* Vector store provider can be swapped without changing core code

---

### Phase 3 — Semantic Query Layer

Goal: Enable meaningful retrieval over codebase.

Deliverables:

* Semantic search service
* Symbol lookup service
* Dependency graph queries
* Ranking strategy for results

Success Criteria:

* Query returns relevant code context
* Cross-file relationships are retrievable

---

### Phase 4 — MCP Interface Layer

Goal: Expose CodeMemory as an MCP server.

Architecture decisions:

* **Transport**: Streamable HTTP (modern MCP transport). SSE is deprecated in MCP 2025-03-26 spec.
* **Library**: `ModelContextProtocol.AspNetCore` — provides `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()` and `MapMcp(...)` middleware
* **Endpoint**: `/api/mcp` (or `/mcp`)
* **Registration**: Tools discovered via `[McpServerToolType]` and `[McpServerTool]` attributes

Deliverables:

* MCP server host integrated into the ASP.NET Core pipeline
* Tool definitions registered via attributes:

  * `semantic_search`
  * `find_related_code`
  * `get_architecture_overview`
  * `trace_dependency`
  * `impact_analysis`
  * `get_edit_context`
* Structured JSON outputs
* CORS configured for MCP client access

Success Criteria:

* External AI agents (Copilot, Codex, OpenCode) can discover and call tools
* Responses are structured and deterministic

---

### Phase 5 — Architecture Intelligence Layer

Goal: Infer higher-level system understanding.

Deliverables:

* Dependency graph builder
* Architecture summarization engine
* Component clustering logic
* Optional git evolution tracking

Success Criteria:

* System can describe architecture in structured form
* Cross-module relationships are explicit

---

## 2a. Task Breakdown Status

| Phase | Status | Reasoning |
|-------|--------|-----------|
| Phase 0 — Foundation | No breakdown needed | Completed as single unit |
| Phase 1 — Indexing Core | 4 distinct sub-systems (crawler, parser, extraction, chunking) — each agent-completable |
| Phase 2 — Storage | **No breakdown needed** | One coherent unit: schema + SQLite vec + `IVectorStore`. Splitting would create artificial seams |
| Phase 3 — Query | **No breakdown needed** | 4 thin services sharing the same store abstraction — tighter together than apart |
| Phase 4 — MCP Interface | **Broken down** → `docs/PHASE4-TASKS.md` | Infrastructure + 4 tools across 4 tasks, with parallel execution opportunities |
| Phase 5 — Architecture Intelligence | **Defer breakdown** | Lowest priority. Contains genuinely distinct sub-systems (graph, clustering, git tracking) — break down if and when this phase is greenlit |

---

## 3. Task Decomposition Rules

Agents MUST break work into TASKS.md using:

### Granularity Rule

Each task MUST:

* Be completable in isolation
* Produce verifiable output
* Not exceed single conceptual unit

---

### Task Format

Each task SHOULD include:

* Objective
* Inputs
* Expected output
* Validation criteria

---

### Dependency Rule

Tasks MUST be ordered such that:

* No task depends on undefined systems
* Each layer builds on previous validated output

---

## 4. Incremental Build Strategy

Agents MUST follow:

1. Make system runnable early
2. Add indexing capability
3. Add storage persistence
4. Add semantic retrieval
5. Expose MCP interface
6. Enhance intelligence layers

NEVER build full system upfront.

---

## 5. Validation Strategy

Each phase MUST include:

* Local execution test
* Minimal functional verification
* Output inspection

No phase is considered complete without observable results.

---

## 6. Risk Control Rules

Agents MUST avoid:

* Over-engineering abstractions early
* Premature multi-repo support
* Complex distributed systems in MVP
* Custom AI frameworks

---

## 7. Success Definition

The system is successful when:

* A repository can be indexed locally
* Semantic queries return meaningful results
* MCP tools expose repository intelligence
* AI agents can reason about code structure using this layer

---

## 8. Final Instruction to Agents

If uncertain, always prioritize:

* working software
* incremental progress
* observable results
* MCP-first exposure

Do not design in abstraction without implementation proof.
