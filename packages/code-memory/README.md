# @uworx/code-memory

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

**CodeMemory** transforms repositories into queryable intelligence — extracting symbols, relationships, and semantic understanding — and exposing it through MCP tools designed for AI coding agents.

## Usage

```bash
# Point at any local repo:
npx @uworx/code-memory --repo /path/to/your/project

# Or run in the current directory:
cd /path/to/your/project
npx @uworx/code-memory
```

This starts an MCP stdio server that reads JSON-RPC from stdin and writes to stdout — compatible with any MCP client (AI coding agents, IDEs, etc.).

## How It Works

The `@uworx/code-memory` package is a lightweight CLI wrapper. On `npm install`, it downloads the platform-specific native binary from GitHub Releases. The binary is a self-contained .NET single-file publish with no runtime dependencies.

## MCP Tools

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
| `sql_query` | SQL queries over indexed data (SELECT/WHERE/ORDER BY/GROUP BY, vector search) |
| `ping` | Health check + indexing status |

All tools return structured JSON.

## Configuration

Configure the MCP tool in your agent's config:

```json
{
  "mcpServers": {
    "CodeMemory": {
      "type": "stdio",
      "command": "npx",
      "args": ["@uworx/code-memory", "--repo", "/path/to/your/project"]
    }
  }
}
```

## Requirements

- Node.js 18+ (for `npx` / `npm install`)
- No .NET runtime required — the binary is self-contained

## Supported Platforms

- **Windows**: x64
- *Linux and macOS: coming soon*

## Indexing Note

Indexing is non-blocking. Poll the `ping` tool until `indexingCompleted` is `true` before calling other tools, or results may be empty/partial.

## Learn More

Full documentation: [github.com/khurram-uworx/CodeMemory](https://github.com/khurram-uworx/CodeMemory)

## License

Apache-2.0
