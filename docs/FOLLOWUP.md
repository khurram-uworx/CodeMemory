# Follow-Up Items

Items discovered during Phase 5 completion audit against PLAN.md. Includes fixes applied and suggestions for future work.

- Items 4, 5, 7, 8, 10, 11 are quick wins if prioritized

---

## Issues Found & Fixed

### `IEditContextService` Not Registered
`EditContextService` was never registered in `Program.cs`, making `get_edit_context` always degrade to `["Edit context service not available"]`.
- **Fix**: Added `builder.Services.AddSingleton<IEditContextService, EditContextService>()` in `Program.cs:47`
- The service itself is well-structured (uses `GetService<T>` internally for all dependencies, handles errors gracefully), was simply missing its DI registration.

---

## Minor Plan Mismatch

### Launch Port
PLAN.md specifies `http://localhost:8080`, but `Properties/launchSettings.json` uses `http://localhost:4792`.
- Low priority — update either the plan or launchSettings to match.
- The actual port used in production is controlled by the hosting environment, not launchSettings.

---

## Intentional Omissions

### `FindRelatedCodeResult.cs` model
Listed in Phase 4 "files likely involved" but not created. `FindRelatedCodeTool` returns `IReadOnlyList<DependencyNode>` directly, which works correctly — a wrapper model adds no value here. The tool meets all acceptance criteria.

---

## Suggestions for Future Work

### 1. SemanticModel-Based Relationship Resolution
Current extractor uses syntax-level identifier matching. A `SemanticModel`-based pass would:
- Resolve overloaded method references accurately
- Detect external/BCL type references (currently omitted)
- Populate `TargetSymbolId` for NuGet package symbols
- **Effort**: Medium. Add optional `SemanticModel` pass after syntax pass.

### 2. Incremental / Watch-Mode Indexing
Currently re-indexes all files on every startup. Could:
- Track file modification timestamps in the index DB
- Watch for file changes via `FileSystemWatcher`
- Only re-parse changed files
- **Effort**: Medium. Requires storage schema addition + file watcher.

### 3. Multi-Language Parsing
- **Completed**: TypeScript, JavaScript, Java via `TreeSitter.DotNet` — full symbol extraction, relationship extraction, and language-aware chunking (PR #...)
- **Remaining**: Python, Go, Rust — each needs a new `TreeSitterParser` grammar registration, plus any language-specific node type mappings in the extractor
- **Effort**: Medium per remaining language (parser and grammar already in `TreeSitter.DotNet`; just add node type mappings and test)

### 4. LLM-Powered Architecture Descriptions
`ArchitectureService` returns raw counts. An LLM layer could:
- Generate natural-language summaries per component
- Describe component responsibilities and relationships
- **Effort**: Low. Add a new service wrapping `IArchitectureService` + `IChatClient`.

### 5. Persistent Git Metric Storage
Git history is cached in-memory with a 5-minute TTL. Could persist to `.index/` DB:
- Per-file commit counts, author counts, last-modified dates
- Avoids O(n) `git log` queries on every cold start
- **Effort**: Low. New storage collection + background updater.

### 6. `TestCoverage` Relationship Population
`FindTestCoverageAsync` falls back to file-name convention (`*Test.cs`). A real test analyzer could produce `TestCoverage` relationships during indexing for precise test-symbol mapping.
- **Effort**: Medium. New extractor or analyzer plugin.

### 7. MCP Resource Endpoints
The MCP spec supports `resources/` (readable data endpoints) in addition to `tools/`. Could expose:
- `codememory://architecture/overview` — structured architecture doc
- `codememory://hotspots` — hotspot ranking
- **Effort**: Low. Wrap existing services as `[McpServerResourceType]`.

### 8. Repository Configuration File
A `.codememory.json` in the repo root could customize:
- Indexing exclusions (beyond `.gitignore`)
- Language detection overrides
- Clustering threshold defaults
- **Effort**: Low. New config model + merge with existing settings.

### 9. Paginated / Memory-Bounded Storage Queries
`ArchitectureService.GetOverviewAsync` loads up to 100K symbols per kind. `ComponentClusteringService` queries relationships per symbol. For repos >100K symbols, pagination or streaming would prevent OOM.
- **Effort**: Medium. Requires new `IStorageService` methods (count, paginated get) or streaming.

### 10. Edit Context Caching
`EditContextService` computes context fresh on every call. Contexts for the same symbol change only when the index changes. An in-memory LRU cache keyed by `(symbolPath, options hash)` would improve latency.
- **Effort**: Low. Add `MemoryCache` wrapper in `EditContextService`.

### 11. Tool Description Polish
Some MCP tool descriptions are minimal (e.g., `get_edit_context`: "Returns edit context for a symbol"). More detailed descriptions (including parameter docs and return shape hints) would help AI agents use tools more effectively.
- **Effort**: Very low. Edit `Description` attributes.
