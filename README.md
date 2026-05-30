# CodeMemory

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

CodeMemory transforms repositories into queryable intelligence — extracting symbols, relationships, and semantic understanding — and exposing it through MCP tools designed for AI coding agents.

What's new in 0.5 and what's coming next:
- [Roadmap & Upcoming Releases](https://github.com/khurram-uworx/CodeMemory/issues/21)

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

## Packages

- [![Npm](https://badgen.net/npm/v/@uworx/code-memory)](https://npmjs.com/package/@uworx/code-memory)
- [![NuGet](https://img.shields.io/nuget/v/CodeMemory)](https://www.nuget.org/packages/CodeMemory)

## Quick Start

### STDIO MCP

Define the MCP tool in your agent's configuration:

```json
  "CodeMemory": {
    "type": "stdio",
    "command": "dotnet",
    "args": ["run", "--project", "src/CodeMemory.Mcp/CodeMemory.Mcp.csproj"]
  }
```

> **Tip:** Prefer a zero-install option? Use the npm package (no .NET SDK required):
> `npx -y @uworx/code-memory`
>
> See the [package docs](packages/code-memory/README.md) for multi-repo configuration and all available options.

### ASP.NET host

Streamable HTTP MCP host with a Repository Dashboard, dynamic repo management, and multi-provider storage.

```bash
dotnet run --project src/CodeMemory.AspNet
```

Features:

- **Repository Dashboard** — Razor Pages UI at `http://localhost:4792/` for managing repos (add by local path or GitHub URL), live SSE status updates, per-repo component browser with classification, and code metrics (symbol distributions, method complexity, coupling analysis)
- **Multi-provider storage** — `"inmemory"`, `"sqlite"`, `"pgvector"`, `"sqlserver"` in `appsettings.json:Storage:Provider`
- **Embedding backends** — `"ngram"` (default, offline), `"onnx"` (bge-micro-v2 via ONNX Runtime), `"ollama"` (Ollama server) in `appsettings.json:Embedding:Provider`
- **Scheduled re-indexing** — cron-based periodic rebuild via `RebuildIndex:Cron`
- **Metrics & Observability** — OpenTelemetry metrics, Prometheus scraping, Grafana dashboards, Aspire Dashboard
- **Docker deployment** — `docker-compose.yml` with PostgreSQL (pgvector), Prometheus, Grafana, Aspire Dashboard

> The [`CodeMemory.AspNet.Extensions`](src/CodeMemory.AspNet.Extensions/) project contains BERT/ONNX and Ollama embedding generators — available for review although not yet wired into the host pipeline.

Each repo gets its own MCP endpoint at `http://localhost:4792/api/mcp/{repoName}`.

> **For agents:** Indexing is non-blocking in both hosts. Poll the `ping` tool until `indexingCompleted` is `true` before calling other tools, or results will be empty/partial.

## Requirements

- .NET 10 SDK or newer

## Learn More

- [GETTING-STARTED](GETTING-STARTED.md) — install, configure, and query your first repo
- [ARCHITECTURE](ARCHITECTURE.md) — system architecture, data flow, dependency layering, storage providers
- [AspNet Host README](src/CodeMemory.AspNet/README.md) — Repository Portal UI, multi-repo architecture, Metrics & Components pages

Blog Posts

- [Transform Repositories into Queryable Intelligence](https://khurram-uworx.github.io/2026/05/20/CodeMemory.html)
- [From Files to Structured Intelligence](https://khurram-uworx.github.io/2026/05/23/CodeMemory2.html)
- [SQL, Extensibility, and the Democratization of Infrastructure](https://khurram-uworx.github.io/2026/05/29/CodeMemory3.html)

## License

MIT
