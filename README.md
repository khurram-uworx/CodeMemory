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
| `sql_query` | SQL queries over indexed data (SELECT/WHERE/ORDER BY/GROUP BY, vector search via `ORDER BY Similarity DESC`) |
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

Storage provider: `"inmemory"` (default, no dependencies) or `"sqlite"` (persistent SQLite with vector extension via `Microsoft.SemanticKernel.Connectors.SqliteVec`). In-memory mode uses `InMemoryVectorStore` from the Memori package — data is lost on restart. SQLite stores vectors in `.memorycode/sqlvec.db` per repo.

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
- **Storage**: In-memory (`InMemoryVectorStore`, default) or SQLite with vector extensions (`Microsoft.SemanticKernel.Connectors.SqliteVec`) — configurable per deployment
- **Parsing**: Roslyn (C#), with language detection for other file types
- **Embeddings**: Memori n-gram embedding generator (offline, no API key) or pluggable via `IEmbeddingGenerator<string, Embedding<float>>`
- **Relationship extraction**: Syntax-based (Inherits, Implements, Calls, References)
- **Git analysis**: Shell git commands with in-memory caching
- **Multi-repo**: `ServiceRegistry` + `StorageServiceRouter` + `IRepoContextAccessor` (AsyncLocal) + MCP `ConfigureSessionOptions` — no keyed DI, no middleware

## Dependencies

Key external packages and version constraints:

- Memori (dual role: `NgramEmbeddingGenerator` for offline embeddings + `InMemoryVectorStore` — chosen for smooth out-of-box experience: no API keys, no model downloads, no database native binaries required for the default configuration)
- Microsoft.CodeAnalaysis.CSharp
- Microsoft.Extensions.AI.Abstractions
- Microsoft.Extensions.VectorData.Abstractions
    - Pinned — `10.1.0` is the highest version compatible with `Microsoft.SemanticKernel.Connectors.SqliteVec 1.74.0-preview` at runtime. Newer `10.x` minors add members to `VectorSearchOptions<T>` (e.g. `OldFilter`) that cause `MissingMethodException` in the SK connector. Bump only when the SK connector's minimum dependency moves past `10.1.0`
- Microsoft.SemanticKernel.Connectors.SqliteVec (optional — only for SQLite storage)
- ModelContextProtocol
- System.Numerics.Tensors
- TreeSitter.DotNet

## Learn More

- [ARCHITECTURE](ARCHITECTURE.md)

## License

Apache-2.0
