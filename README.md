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

CodeMemory indexes a repository and exposes 10 MCP tools:

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
| `ping` | Health check |

All tools return structured JSON. No freeform prompts, no chat — pure deterministic repository intelligence.

## Quick Start

```bash
# Build and run — zero config, no API keys needed
dotnet run --project src/CodeMemory

# Starts an MCP server at http://localhost:4792/api/mcp
# Connect any MCP client to discover all 10 tools.
```

CodeMemory indexes your repository on startup, extracts symbols, relationships, and semantic chunks. Built-in embedding generator works immediately — no model downloads or external services required.

## Requirements

- .NET 10 SDK or newer

## Architecture

- **Host**: ASP.NET Core with MCP over Streamable HTTP
- **Storage**: SQLite with vector extensions via `Microsoft.SemanticKernel.Connectors.SqliteVec`
- **Parsing**: Roslyn (C#), with language detection for other file types
- **Embeddings**: Built-in n-gram generator or pluggable via `IEmbeddingGenerator<string, Embedding<float>>`
- **Relationship extraction**: Syntax-based (Inherits, Implements, Calls, References)
- **Git analysis**: Shell git commands with in-memory caching

## Learn More

- [ARCHITECTURE](ARCHITECTURE.md)

## License

Apache-2.0
