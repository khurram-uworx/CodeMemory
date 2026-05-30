# @uworx/code-memory

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

**code-memory** transforms repositories into queryable intelligence — extracting symbols, relationships, and semantic understanding — and exposing it through MCP tools designed for AI coding agents.

# Installation

From your terminal run:

```bash
npx -y @uworx/code-memory --help
```

It should download latest binaries when needed and print usage info and exit. You can then proceed to configure your MCP client (VS Code, Cursor, Claude Desktop, etc.) to use the `code-memory` MCP server as described below.

## MCP Server Setup

Configure the MCP server in your client (VS Code, Cursor, Claude Desktop, etc.) by adding the following to your MCP settings:

```json
{
  "mcpServers": {
    "code-memory": {
      "command": "npx",
      "args": ["-y", "@uworx/code-memory"]
    }
  }
}
```

This indexes the current working directory. To index a different folder, pass `--repo` (each argument as a separate array element):

```json
{
  "mcpServers": {
    "code-memory": {
      "command": "npx",
      "args": ["-y", "@uworx/code-memory", "--repo", "/path/to/your/project"]
    }
  }
}
```

## Repository Configuration

Each repository can be initialized with a `.codememory.json` configuration file at its root by running:

```bash
npx -y @uworx/code-memory --init
```

This creates a `.codememory.json` file (and adds it to `.gitignore` if needed) with all default settings. The file contains per-repo configuration such as file exclusion patterns, language overrides, and clustering thresholds. You can edit it at any time — optional fields that are removed fall back to sensible defaults.

## How It Works

The `@uworx/code-memory` package is a lightweight CLI wrapper. On `npm install`, it downloads the platform-specific native binary from GitHub Releases. The binary is a self-contained .NET single-file publish with no runtime dependencies.

## MCP Tools

| Tool | What it gives you |
|---|---|
| `ping` | Health check + indexing status (`indexingCompleted`, `fileWatcherActive`) |
| `semantic_search` | Natural language code search with optional similarity threshold |
| `trace_dependency` | Symbol dependency tracing (upstream/downstream/both, configurable depth) |
| `get_architecture_overview` | Component structure, language breakdown, file/symbol counts |
| `get_edit_context` | Source code, dependency chains, related symbols, and test coverage for a symbol |
| `find_related_code` | Related symbols via dependency graph (breadth-first, filterable by type) |
| `impact_analysis` | Change impact — downstream dependencies, affected files, components, test coverage |
| `get_component_clusters` | Logical groupings based on inter-component coupling density |
| `get_symbol_history` | Git commit history for a symbol (commits, authors, dates) |
| `get_hotspots` | Most frequently changed files ranked by commit count |
| `sql_query` | SQL queries over indexed data — SELECT/WHERE/ORDER BY/GROUP BY/HAVING, CTEs, derived tables, aggregates, vector search via `ORDER BY Similarity DESC` |
| `rescan_repository` | Trigger full re-index (clear all data, re-scan, re-store) |
| `get_repository_root` | Returns the root path of the currently active repository |

All tools return structured JSON.

## Requirements

- Node.js 18+ (for `npx` / `npm install`)
- No .NET runtime required — the binary is self-contained

## Supported Platforms

- **Windows**: x64
- **Linux**: x64
- *macOS: coming soon*

## Indexing Note

Indexing is non-blocking. Poll the `ping` tool until `indexingCompleted` is `true` before calling other tools, or results may be empty/partial.

## `.codememory` Folder

CodeMemory creates a `.codememory` folder in the target repository root to store index data, logs (`Log.*.txt`), and — in future versions — additional cached artifacts. This folder is managed entirely by CodeMemory and is kept clean. You can safely add `.codememory/` to your project's `.gitignore`; the MCP server handles cleanup internally.

## Learn More

Full documentation: [github.com/khurram-uworx/CodeMemory](https://github.com/khurram-uworx/CodeMemory)

## License

MIT
