# @uworx/code-memory

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

**CodeMemory** transforms repositories into queryable intelligence — extracting symbols, relationships, and semantic understanding — and exposing it through MCP tools designed for AI coding agents.

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

## Requirements

- Node.js 18+ (for `npx` / `npm install`)
- No .NET runtime required — the binary is self-contained

## Supported Platforms

- **Windows**: x64
- *Linux and macOS: coming soon*

## Indexing Note

Indexing is non-blocking. Poll the `ping` tool until `indexingCompleted` is `true` before calling other tools, or results may be empty/partial.

## `.codememory` Folder

CodeMemory creates a `.codememory` folder in the target repository root to store index data, logs (`Log.*.txt`), and — in future versions — additional cached artifacts. This folder is managed entirely by CodeMemory and is kept clean. You can safely add `.codememory/` to your project's `.gitignore`; the MCP server handles cleanup internally.

## Learn More

Full documentation: [github.com/khurram-uworx/CodeMemory](https://github.com/khurram-uworx/CodeMemory)

## License

Apache-2.0
