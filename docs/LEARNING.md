# LEARNING

## Context

Multi-repo refactoring for CodeMemory. A single `CodeMemory.AspNet` server hosts N repos, each exposed via URL-based routing: `/api/mcp/{repoName}`.

---

## What Worked

### Multi-repo URL routing (Streamable HTTP)

- `Stateless = true` + `ConfigureSessionOptions` extracts repo name from URL path → sets `IRepoContextAccessor.CurrentRepoName` via `AsyncLocal`
- Each repo gets its own MCP endpoint: `/api/mcp/codememory`, `/api/mcp/memori`, `/api/mcp/test`
- Coding agent (opencode) uses `"type": "remote"` → Streamable HTTP POST — works with this setup
- Verified: memori repo endpoint confirmed working by user

### Storage initialization

- `IndexingHostedService` initializes all storage services upfront before sequential indexing
- Fixes `throwIfNotInitialized()` race when tool calls arrive before indexing reaches a repo

### Test stability

- 148/148 tests passing
- `test` repo in `appsettings.Development.json` points to `"."` (AspNet project dir)
- All MCP tool tests use `/api/mcp/test`

### stdio MCP
- `CodeMemory.Mcp` project — stdio transport, single repo, takes `--repo <path>`
- Builds and works independently of the ASP.NET server

---

## What Did NOT Work (and why)

### SSE transport via SDK

- The MCP SDK (`ModelContextProtocol.AspNetCore` v1.3.0) cannot combine `Stateless = true` with `EnableLegacySse = true` (throws at startup)
- Switching to stateful (`Stateless = false`) breaks all tests — they don't send MCP `initialize` handshake
- **Red herring**: opencode (and most modern agents) use Streamable HTTP, not SSE. No SSE changes needed.

### Hardcoded `/api/mcp/default` in tests

- After removing the fallback `default` repo, all MCP tool tests failed with 404
- Fixed: changed to `/api/mcp/test` and added `test` repo to config

---

## Architecture Rules Discovered

| Rule | Why |
|------|-----|
| `StorageService` requires `InitializeAsync()` before any read/write | `throwIfNotInitialized()` throws otherwise |
| `ConfigureSessionOptions` must set `IRepoContextAccessor.CurrentRepoName` from URL | `StorageServiceRouter.GetStorage()` uses it to find the right per-repo storage |
| `IRepoContextAccessor` uses `AsyncLocal<string?>` | Flows with `ExecutionContext`, works with `PerSessionExecutionContext = true` |
| Repo-relative paths resolve from `Environment.CurrentDirectory` | At runtime it's the AspNet project dir; during `WebApplicationFactory` tests it's the test bin dir |
| Query the MCP SDK XML docs directly (in NuGet cache) | NuGet.org search returns Azure Functions MCP docs, not ASP.NET Core SDK docs |
