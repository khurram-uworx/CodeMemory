# Follow-Up Items

---

## Suggestions for Future Work

### 1. SemanticModel-Based Relationship Resolution
**Status: Not started. No `SemanticModel` usage anywhere in the codebase.**

Current extractor uses syntax-level identifier matching. A `SemanticModel`-based pass would:
- Resolve overloaded method references accurately
- Detect external/BCL type references (currently omitted)
- Populate `TargetSymbolId` for NuGet package symbols
- **Effort**: Medium. Add optional `SemanticModel` pass after syntax pass.

---

### 2. Incremental / Watch-Mode Indexing
**Status: Not started. Full re-index on every startup. No `FileSystemWatcher` or incremental parsing.**

Could:
- Track file modification timestamps in the index DB
- Watch for file changes via `FileSystemWatcher`
- Only re-parse changed files
- **Effort**: Medium. Requires storage schema addition + file watcher.

---

### 3. Multi-Language Parsing
**Status: Partially done.**

- **Completed**: TypeScript, JavaScript, Java via `TreeSitter.DotNet` — full symbol extraction, relationship extraction, and language-aware chunking. Supported languages in `LanguageDetector`: `CSharp, TypeScript, JavaScript, Java`.
- **Remaining**: Python, Go, Rust — each needs a new `TreeSitterParser` grammar registration, plus language-specific node type mappings in the extractor, and entries in the `Language` enum and `LanguageDetector` extension map.
- **Effort**: Medium per remaining language (parser and grammar already in `TreeSitter.DotNet`; just add node type mappings and test).

---

### 4. LLM-Powered Architecture Descriptions
**Status: Not started. No `IChatClient` usage. `ArchitectureService` returns raw counts.**

An LLM layer could:
- Generate natural-language summaries per component
- Describe component responsibilities and relationships
- **Effort**: Low. Add a new service wrapping `IArchitectureService` + `IChatClient`.

---

### 5. Persistent Git Metric Storage
**Status: Not started. Git history cached in-memory (`ConcurrentDictionary`) with 5-minute TTL.**

Could persist to `.memorycode/` DB:
- Per-file commit counts, author counts, last-modified dates
- Avoids O(n) `git log` queries on every cold start
- **Effort**: Low. New storage collection + background updater.

---

### 6. `TestCoverage` Relationship Population
**Status: Not started. `FindTestCoverageAsync` falls back to file-name convention (`*Test.cs`).**

The code does check for stored `TestCoverage` relationships first (`DependencyGraphService.cs:97`), but no extractor populates them. A real test analyzer could produce `TestCoverage` relationships during indexing for precise test-symbol mapping.
- **Effort**: Medium. New extractor or analyzer plugin.

---

### 7. MCP Resource Endpoints
**Status: Not started. No `McpServerResourceType` usage anywhere.**

The MCP spec supports `resources/` (readable data endpoints) in addition to `tools/`. Could expose:
- `codememory://architecture/overview` — structured architecture doc
- `codememory://hotspots` — hotspot ranking
- **Effort**: Low. Wrap existing services as `[McpServerResourceType]`.

---

### 8. Repository Configuration File
**Status: Not started. No `.codememory.json` found anywhere in the repo.**

A `.codememory.json` in the repo root could customize:
- Indexing exclusions (beyond `.gitignore`)
- Language detection overrides
- Clustering threshold defaults
- **Effort**: Low. New config model + merge with existing settings.

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
