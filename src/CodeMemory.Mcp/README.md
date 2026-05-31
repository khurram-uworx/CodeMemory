# CodeMemory.Mcp

MCP server adapter for [CodeMemory](https://github.com/khurram-uworx/CodeMemory) — exposes repository intelligence as Model Context Protocol tools over STDIO transport for AI coding agents.

## Installation (recommended)

Via NPM (no .NET runtime required):

```bash
npx -y @uworx/code-memory --help
```

The NPM package is a lightweight downloader. On first use it fetches a self-contained .NET binary for your platform.

## Usage

```bash
# Default — index current directory and serve MCP tools
npx -y @uworx/code-memory

# Target a specific repository
npx -y @uworx/code-memory --repo C:\Projects\MyApp

# Debug mode — index synchronously with verbose logging (no MCP server)
npx -y @uworx/code-memory --repo ./my-project --debug

# All flags:
#   --repo, -r <path>    Repository root path (default: current directory)
#   --init, -i           Create .codememory.json with defaults and add to .gitignore
#   --debug              Index synchronously with verbose logging
#   --version            Show version
#   --help, -h           Show this help
```

## Global dotnet tool (alternative)

```bash
dotnet tool install -g CodeMemory.Mcp --prerelease
code-memory
```

## MCP Tools

| Tool | Description |
|------|-------------|
| `ping` | Health check — returns indexing status and progress |
| `semantic_search` | Search code semantically related to a natural language query |
| `sql_query` | Execute SELECT-only SQL against the indexed repository |
| `get_architecture_overview` | High-level repo structure overview |
| `get_component_clusters` | Component grouping by dependency density |
| `get_hotspots` | Most frequently changed files |
| `get_symbol_history` | Git commit history for a symbol |
| `trace_dependency` | Trace dependency chains for a symbol |
| `impact_analysis` | Analyze impact of changing a symbol |
| `get_edit_context` | Comprehensive edit context for a symbol |
| `find_related_code` | Find related symbols by relation type |
| `get_repository_root` | Active repository root path |
| `rescan_repository` | Trigger full re-index of the repository |

## MCP Client Registration

**Cursor / VS Code (Cline) / Claude Desktop:**

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

## License

MIT
