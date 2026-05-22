# CodeMemory

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

CodeMemory transforms repositories into queryable intelligence — extracting symbols, relationships, and semantic understanding — and exposing it through MCP tools designed for AI coding agents.

## When to Use

For AI coding agents that need deep, persistent understanding of a codebase. Not a fit for human-focused search tools, CLI utilities, or standalone chat interfaces.

## Core Idea

```
User asks: "How does authentication work here?"
CodeMemory returns: structured, multi-file reasoning across symbols, dependencies, and semantics.
```

Instead of searching code, CodeMemory enables **understanding codebases**.

## Capabilities

- **Semantic code understanding** — query code in natural language, get symbol-aware results across files
- **Dependency & impact analysis** — trace what depends on what, upstream and downstream; analyze change impact before editing
- **SQL query engine over code** — SELECT, WHERE, GROUP BY, aggregates, CTEs, derived tables, even vector search via `ORDER BY Similarity DESC`
- **Git & architecture intelligence** — hotspots, symbol history, component clusters, architecture overviews
- **Edit context** — grab source code, dependencies, and tests for any symbol in one shot

All tools return structured JSON. No freeform prompts, no chat.

## Quick Start

### Single repo (stdio — Windows only)

Define the MCP tool in your agent's configuration:

```json
  "CodeMemory": {
    "type": "stdio",
    "command": "dotnet",
    "args": ["run", "--project", "src/CodeMemory.Mcp/CodeMemory.Mcp.csproj"]
  }
```

### ASP.NET host (Streamable HTTP — experimental)

Next release focus. Try it now:

```bash
dotnet run --project src/CodeMemory.AspNet
```

Configure repos in `appsettings.json` with Storage provider (`"inmemory"`, `"sqlite"`, `"pgvector"`, `"sqlserver"`). Each repo gets its own MCP endpoint at `http://localhost:4792/api/mcp/{repoName}`.

> **For agents:** Indexing is non-blocking in both hosts. Poll the `ping` tool until `indexingCompleted` is `true` before calling other tools, or results will be empty/partial.

## Requirements

- .NET 10 SDK or newer

## Learn More

- [GETTING-STARTED](GETTING-STARTED.md) — install, configure, and query your first repo
- [ARCHITECTURE](ARCHITECTURE.md) — system architecture, data flow, dependency layering, storage providers

## License

Apache-2.0
