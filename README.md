# CodeMemory

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

CodeMemory transforms repositories into queryable intelligence — extracting symbols, relationships, and semantic understanding — and exposing it through MCP tools designed for AI coding agents.

## When to Use

- AI coding agents that need deep, persistent understanding of a codebase
- IDE assistants that want architecture-aware context
- Developer tools that require semantic code search beyond grep
- Teams building AI-powered code review, refactoring, or impact analysis

**Not a fit for:** human-focused search tools, CLI utilities for developers, or standalone chat interfaces.

## Core Idea

```
User asks: "How does authentication work here?"
CodeMemory returns: structured, multi-file reasoning across symbols, dependencies, and semantics.
```

Instead of searching code, CodeMemory enables **understanding codebases**.

## What It Does

CodeMemory indexes a repository and exposes MCP tools:

| Tool | What it gives you |
|---|---|
| `semantic_search` | Natural language code search |
| `trace_dependency` | What depends on what (upstream/downstream) |
| `get_architecture_overview` | Component structure, language breakdown |
| `get_edit_context` | Source code + deps + tests for a symbol |
| `find_related_code` | Related symbols by relationship type |
| `impact_analysis` | Change impact: affected files, components, tests |
| `get_component_clusters` | Logical groupings by inter-component coupling |
| `get_symbol_history` | Git commit history for a symbol |
| `get_hotspots` | Most frequently changed files |
| `sql_query` | SQL queries over indexed data (SELECT/WHERE/ORDER BY/GROUP BY/HAVING, CTEs, derived tables, aggregates, vector search via `ORDER BY Similarity DESC`) |
| `ping` | Health check + indexing status (`indexingCompleted: true/false`) |

All tools return structured JSON. No freeform prompts, no chat — pure deterministic repository intelligence.

## Quick Start

### Single repo (stdio — for agents)

Define the MCP tool in your agent's configuration:

```json
  "CodeMemory": {
    "type": "stdio",
    "command": "dotnet",
    "args": ["run", "--project", "src/CodeMemory.Mcp/CodeMemory.Mcp.csproj"]
  }
```

### ASP.NET host (Streamable HTTP — for remote agents)

```bash
dotnet run --project src/CodeMemory.AspNet
# Starts at http://localhost:4792 — repos available at /api/mcp/{repoName}
```

Configure repos in `src/CodeMemory.AspNet/appsettings.json`:

```json
{
  "Repositories": {
    "repo1": "C:\\Projects\\my-app",
    "repo2": "C:\\Projects\\my-lib"
  },
  "Storage": {
    "Provider": "inmemory"
  }
}
```

Storage provider: `"inmemory"` (default, no dependencies), `"sqlite"` (persistent SQLite), `"pgvector"` (PostgreSQL with pgvector), or `"sqlserver"` (SQL Server). In-memory mode uses `InMemoryVectorStore` from the Memori package — data is lost on restart. SQLite stores vectors in `.memorycode/sqlvec.db` per repo. `pgvector` and `sqlserver` providers use `HybridStorageService` — symbols/relationships in EF Core relational tables, chunks in the vector store.

Each repo gets its own URL:

```bash
POST http://localhost:4792/api/mcp/repo1   # JSON-RPC to repo1
POST http://localhost:4792/api/mcp/repo2   # JSON-RPC to repo2
```

The root route (`GET /`) returns storage provider, per-repo indexing status, and registry info.

> **For agents:** Indexing is non-blocking in both hosts. Poll the `ping` tool until `indexingCompleted` is `true` before calling other tools, or results will be empty/partial.

## Requirements

- .NET 10 SDK or newer

## Architecture

- **Host**: ASP.NET Core with MCP over Streamable HTTP
- **Storage**: Pluggable — in-memory (`InMemoryVectorStore`, default, zero-dependency), SQLite (`Microsoft.SemanticKernel.Connectors.SqliteVec`), PostgreSQL with pgvector, or SQL Server. Relational providers use `HybridStorageService` — symbols/relationships in EF Core tables, chunks in the vector store.
- **Parsing**: Roslyn (C#), with language detection for other file types
- **Embeddings**: Memori n-gram embedding generator (offline, no API key) or pluggable via `IEmbeddingGenerator<string, Embedding<float>>`
- **Relationship extraction**: Syntax-based (Inherits, Implements, Calls, References)
- **Git analysis**: Shell git commands with in-memory caching
- **Multi-repo**: `ServiceRegistry` + `StorageServiceRouter` + `IRepoContextAccessor` (AsyncLocal) + MCP `ConfigureSessionOptions` — no keyed DI, no middleware

## Dependencies

Key external packages and version constraints:

- **Memori** — dual role: `NgramEmbeddingGenerator` for offline embeddings + `InMemoryVectorStore`. No API keys, no model downloads, no database native binaries required for the default configuration.
- **Microsoft.EntityFrameworkCore** — relational storage for symbols/relationships in the AspNet hybrid storage path.
- **SqlParserCS** — SQL parsing for both Mcp (LINQ over InMemoryVectorStore) and AspNet (validation + table translation) query paths.
- **Microsoft.Extensions.AI.Abstractions** — `IEmbeddingGenerator`, `IChatClient` abstractions.
- **Microsoft.Extensions.VectorData.Abstractions** — vector store abstractions.
    - Pinned — `10.1.0` is the highest version compatible with `Microsoft.SemanticKernel.Connectors.SqliteVec 1.74.0-preview` at runtime. Newer `10.x` minors add members to `VectorSearchOptions<T>` (e.g. `OldFilter`) that cause `MissingMethodException` in the SK connector. Bump only when the SK connector's minimum dependency moves past `10.1.0`
- **Microsoft.CodeAnalysis.CSharp** — Roslyn-based C# parsing.
- **ModelContextProtocol** — MCP server types and tool attributes.
- **ModelContextProtocol.AspNetCore** — MCP Streamable HTTP transport (AspNet host only).
- **TreeSitter.DotNet** — multi-language parsing (TS, JS, Java).
- **System.Numerics.Tensors** — `TensorPrimitives.Norm` for embedding normalization.
- **Microsoft.SemanticKernel.Connectors.SqliteVec** (optional — SQLite vector storage).
- **Microsoft.SemanticKernel.Connectors.PgVector** (optional — PostgreSQL vector storage).
- **Microsoft.SemanticKernel.Connectors.SqlServer** (optional — SQL Server vector storage).

## Learn More

- [ARCHITECTURE](ARCHITECTURE.md)

## License

Apache-2.0
