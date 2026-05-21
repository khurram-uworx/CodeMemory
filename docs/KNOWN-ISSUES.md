# Known Issues

---

## 1. `ComponentMapping` Static Cache Breaks Multi-Repo Component Resolution

**Status: Confirmed. Root cause identified, not yet fixed.**

`ComponentMapping` (`src/CodeMemory/Services/Architecture/ComponentMapping.cs`) is a **static** class with a `static ConcurrentDictionary` storing file path prefix → component name mappings. It is populated during indexing by `IndexingEngine.RunIndexingAsync()` calling `ComponentMapping.Initialize()`.

**Problem:** In a multi-repo ASP.NET deployment, `ComponentMapping` is shared across all repos. When any repo undergoes a full reindex, `Initialize()` calls `prefixToComponent.Clear()` and repopulates from only that repo's project files. This **destroys all other repos' component mappings**, causing `ArchitectureService` and `ComponentClusteringService` to fall back to directory-depth-based component names for those repos instead of the correct project-derived names.

**Affected:**
- `ArchitectureService.GetOverviewAsync()` — components misnamed for unaffected repos
- `ComponentClusteringService.GetClustersAsync()` — same
- All MCP tools that consume component names

**Root cause:**
- Static state (`ComponentMapping`) should either be per-repo (keyed by repo name) or `ComponentMapping.Initialize()` should merge rather than replace
- No existing mechanism to persist/restore component mappings per repo

**Potential fixes (not implemented):**
- Make `ComponentMapping` instance-based with repo-scoped lifetime (requires DI changes and a per-repo registry)
- Change `Initialize()` to accept a repo name key and store mappings in a `ConcurrentDictionary<string, ConcurrentDictionary<string, string>>` keyed by repo
- Persist component mappings in storage (add `ComponentMappingRecord` to storage schema)

---

## 2. MCP Resource Endpoints
**Status: Not started. No `McpServerResourceType` usage anywhere.**

The MCP spec supports `resources/` (readable data endpoints) in addition to `tools/`. Could expose:
- `codememory://architecture/overview` — structured architecture doc
- `codememory://hotspots` — hotspot ranking
- **Effort**: Low. Wrap existing services as `[McpServerResourceType]`.

---

## 3. Paginated / Memory-Bounded Storage Queries
**Status: Not started. `IStorageService` has `int top = 100` defaults on `GetSymbolsByKindAsync` and `GetSymbolsByFileAsync`, but `ArchitectureService.GetOverviewAsync` loads up to 100K symbols per kind. `ComponentClusteringService` queries relationships per symbol.**

For repos >100K symbols, pagination or streaming would prevent OOM.
- `IStorageService` currently has no count or paginated get methods
- **Effort**: Medium. Requires new `IStorageService` methods (count, paginated get) or streaming.

---

## 4. Edit Context Caching
**Status: Not started. `EditContextService` computes context fresh on every call. No `MemoryCache` or `IMemoryCache`.**

Contexts for the same symbol change only when the index changes. An in-memory LRU cache keyed by `(symbolPath, options hash)` would improve latency.
- **Effort**: Low. Add `MemoryCache` wrapper in `EditContextService`.

---

## 5. Uncomment/Implement `RescanRepositoryAsync` MCP Tool
**Status: Partially started. `AdminTool.cs` exists but has all tool methods commented out.**

There is an `AdminTool` with `[McpServerToolType]` that contains a fully drafted (but commented-out) `RescanRepositoryAsync` method and a `GetRepositoryRoot` method. The tool would let agents trigger a full re-index on demand. Currently:
- `IIndexingService` is commented out as a dependency
- No re-trigger mechanism exists (only startup indexing via `IndexingHostedService` or `Task.Run`)
- A working rescan tool would give agents a way to recover from stale indexes without restarting the server
- **Effort**: Low-Medium. Uncomment, add `IIndexingService` interface + implementation, wire up in DI.
