# Follow-Up Items

---

## Suggestions for Future Work

### 7. MCP Resource Endpoints
**Status: Not started. No `McpServerResourceType` usage anywhere.**

The MCP spec supports `resources/` (readable data endpoints) in addition to `tools/`. Could expose:
- `codememory://architecture/overview` — structured architecture doc
- `codememory://hotspots` — hotspot ranking
- **Effort**: Low. Wrap existing services as `[McpServerResourceType]`.

---

### 9. Paginated / Memory-Bounded Storage Queries
**Status: Not started. `IStorageService` has `int top = 100` defaults on `GetSymbolsByKindAsync` and `GetSymbolsByFileAsync`, but `ArchitectureService.GetOverviewAsync` loads up to 100K symbols per kind. `ComponentClusteringService` queries relationships per symbol.**

For repos >100K symbols, pagination or streaming would prevent OOM.
- `IStorageService` currently has no count or paginated get methods
- **Effort**: Medium. Requires new `IStorageService` methods (count, paginated get) or streaming.

---

### 10. Edit Context Caching
**Status: Not started. `EditContextService` computes context fresh on every call. No `MemoryCache` or `IMemoryCache`.**

Contexts for the same symbol change only when the index changes. An in-memory LRU cache keyed by `(symbolPath, options hash)` would improve latency.
- **Effort**: Low. Add `MemoryCache` wrapper in `EditContextService`.

---

### 11. Uncomment/Implement `RescanRepositoryAsync` MCP Tool
**Status: Partially started. `AdminTool.cs` exists but has all tool methods commented out.**

There is an `AdminTool` with `[McpServerToolType]` that contains a fully drafted (but commented-out) `RescanRepositoryAsync` method and a `GetRepositoryRoot` method. The tool would let agents trigger a full re-index on demand. Currently:
- `IIndexingService` is commented out as a dependency
- No re-trigger mechanism exists (only startup indexing via `IndexingHostedService` or `Task.Run`)
- A working rescan tool would give agents a way to recover from stale indexes without restarting the server
- **Effort**: Low-Medium. Uncomment, add `IIndexingService` interface + implementation, wire up in DI.
