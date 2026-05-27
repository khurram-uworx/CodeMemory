# Known Issues

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


