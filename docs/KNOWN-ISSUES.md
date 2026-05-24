# SQL Query — Known Issues & Limitations

| # | Issue | Effort | Impact | Status |
|---|-------|--------|--------|--------|
| 3 | JOINs, UNION, WHERE subqueries | Large | High | Phase 1+2 — full join-type-aware pipeline with INNER/LEFT/RIGHT/FULL OUTER JOIN, USING(col), nested joins, WHERE subqueries (`IN (SELECT ...)`), and UNION/INTERSECT/EXCEPT all implemented. See "Runtime / Execution Gaps" section below for implementation details. |
| 6 | `materializeAsync` / `toAsyncEnumerable` deep reflection (~80 lines) | Medium | Medium | Won't Do — generic bridge applied |
| 9 | ORDER BY boxes via `GetValueOrDefault` | Small | Low | Won't Do — see note |
| 10 | `getConstantString` compiles throwaway expression | Small | Low | Won't Do — interpreted fallback applied |
| 11 | `TableSchemaProvider` not wired in | Small\* | Low | |

\* Wiring `TableSchemaProvider` itself is small; it is only needed when JOINs land (Large item).

---

## Parse / Syntax Gaps

### 3 JOINs, UNION, WHERE subqueries

**Phase 1 Complete — Multi-table FROM and cross-join/inner-join support.**

JOINs (including explicit `JOIN ... ON`) are now supported via an in-memory cartesian product + filter execution model. All column name resolution works with qualified (`s.Name`) and unqualified (`Name`) references — `CompoundIdentifier` expressions try the full `alias.col` key first and fall back to `col` for backward compatibility.

**Implemented:**
- Comma-separated FROM (`FROM t1, t2`) — parses multiple `TableWithJoins` entries
- Explicit JOIN syntax (`FROM t1 JOIN t2 ON condition`, `LEFT JOIN`, `CROSS JOIN`) — ON expressions are AND-combined with WHERE
- Self-joins (`FROM SymbolRecord c, SymbolRecord m`) — same table with different aliases
- CTE + JOIN composition (`WITH cte AS (...) SELECT ... FROM cte, other_table`)
- Table alias qualified columns (`s.Name`, `r.TargetSymbolId`) via `CompoundIdentifier` fallback
- GROUP BY, ORDER BY, aggregates, HAVING, LIMIT all work on merged result sets
- Vector search rejected with clear error on multi-table queries
- `TableSchemaProvider` (item 11) wired in

**Architecture:** `SqlQueryService.cs` detects multi-table queries via `detectMultiTable()`, dispatches to `executeJoinQueryAsync()` which: (1) flattens all `TableWithJoins` + `Joins` into a `TableRef` list via `parseFromClause()`, (2) fetches all rows from each collection (or CTE) with `alias.`-prefixed column names, (3) computes the cartesian product via `cartesianMerge()`, (4) applies the combined WHERE + ON filter via in-memory `evaluateExpression()` on merged dictionaries. `ON` conditions are extracted from `JoinConstraint` and merged via `mergeOnConditions()`. The `rowGetValue()` helper provides qualified→unqualified column fallback in `applyGroupBy`, `makeSortSelector`, and `projectRows`.

**Phase 2 Complete — All items implemented:**
- Proper INNER JOIN optimization (filter-while-merging instead of full cartesian)
- LEFT/RIGHT/FULL OUTER JOIN semantics (preserving unmatched rows with NULL filling)
- `USING(col)` shorthand (auto-generates `left.col = right.col` conditions)
- Nested joins (parenthesized joins via `TableFactor.NestedJoin` recursion)
- Subqueries in WHERE clause (`WHERE col IN (SELECT ...)` via `InSubquery` materialization)
- UNION / INTERSECT / EXCEPT (recursive `SetOperation` evaluation with dedup)
- `TableSchemaProvider` join-key metadata annotations

**Architecture refactored:** Flat `cartesianMerge` replaced with join-type-aware pipeline:
1. `evaluateGroupAsync` processes each `TableWithJoins` as a group with sequential join application
2. `mergeWithJoinType` dispatches to `crossJoin`/`innerJoin`/`leftJoin`/`fullOuterJoin` with ON-evaluation during merge
3. `extractJoinInfo` handles `ConstrainedJoinOperator` (INNER/LEFT/RIGHT/FULL) and `CrossJoin`
4. `generateUsingCondition` converts `USING(col)` to equality expressions
5. `materializeSubqueriesAsync` executes `InSubquery` nodes before synchronous filter
6. `executeSetOperationAsync` recursively evaluates `SetOperation` for UNION/INTERSECT/EXCEPT

---

## Runtime / Execution Gaps

### 6 `materializeAsync` / `toAsyncEnumerable` use deep reflection to enumerate `IAsyncEnumerable<T>`

Both methods manually invoked `IAsyncEnumerable<T>` via `MethodInfo.Invoke` — `GetAsyncEnumerator`, `MoveNextAsync`, `GetAwaiter`/`GetResult`, `DisposeAsync` — instead of using `await foreach`. This was ~80 lines of fragile reflection duplicated across two methods.

**Root cause:** The generic type argument is erased at this call site because `GetAsync` and `SearchAsync` are found via reflection. The return type is `object`, so `await foreach` cannot be used directly.

**Resolved:** Replaced both methods with a generic bridge pattern. Each now has a clean generic helper using `await foreach` (`materializeAsyncCore<T>`, `toAsyncEnumerableCore<T>`) and a 4-line bridge method that dispatches via `MakeGenericMethod`. The fragile per-method reflection (`GetAwaiter`, `GetResult`, `DisposeAsync`) is completely eliminated — the only remaining reflection is a single `GetInterface` + `MakeGenericMethod` call.

**Evidence** — Microsoft Learn recommends generic collections/interfaces to avoid the performance and fragility of untyped reflection:
> *"Use generic collections instead of nongeneric collections... Generic collections prevent type errors at runtime and also avoid boxing for value types."*
> — [Generic types and methods (Microsoft Learn)](https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics#consuming-generic-types)

Additionally, `toAsyncEnumerable` previously leaked its `IAsyncEnumerator` (no `DisposeAsync` call) — the new `await foreach` in the generic helper handles disposal correctly via the compiler-generated async state machine.

---

## Performance

### 9 ORDER BY boxes all values via `GetValueOrDefault`

`applyOrderBy` uses `r.GetValueOrDefault(sortColumn)` which returns `object?`, boxing every value type for comparison against the sort key. Acceptable at repo scale but doubles allocation pressure on sorted columns.

**Won't Do** — Boxing is inherent to `Dictionary<string, object?>` as the row representation. The entire SQL query engine relies on untyped dictionaries because column types are resolved at runtime from parsed SQL, not known at compile time. Fixing this would require a fundamentally different row model (typed columnar storage or type-aware row abstraction), which is a major architectural change out of scope for this feature.

**Evidence** — Microsoft Learn confirms generic collections avoid boxing, but that would require replacing the untyped dictionary model:
> *"Always use generic collections instead of nongeneric collections... Generic collections prevent type errors at runtime and also avoid boxing for value types, which improves performance."*
> — [Generic types and methods (Microsoft Learn)](https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics#consuming-generic-types)

The only practical mitigation at the current abstraction — runtime type-dispatch in the sort selector — would add complexity branches for marginal gain at repo-scale row counts.

### 10 `getConstantString` compiles a throwaway expression per LIKE pattern

`SqlExpressionBuilder.cs:94` falls through to `LinqExpr.Lambda<Func<string>>(expr).Compile()()` when the LIKE pattern is not a raw `ConstantExpression`. This compiles a new delegate on every LIKE/ILIKE evaluation.

In practice, the LIKE/ILIKE pattern expression is always a new tree (different pattern text each call), so caching would not help. The only case to optimize is the first branch in `getConstantString` which checks for `ConstantExpression` directly — this succeeds 100% of the time for patterns that are string literals in SQL. The fallback `Compile()` path only triggers if the pattern expression is a computed expression (extremely rare in real-world SQL).

**Won't Do** — Changed fallback to `Compile(preferInterpretation: true)` so the rare computed-pattern case uses expression tree interpretation instead of JIT/AOT compilation. This is a net10.0 project, so `preferInterpretation` is available (since .NET 6).

**Evidence** — Microsoft Learn explicitly advises against caching compiled expressions:
> *"Don't create any more sophisticated caching mechanisms to increase performance by avoiding unnecessary compile calls. Comparing two arbitrary expression trees to determine if they represent the same algorithm is a time consuming operation."*
> — [Execute expression trees (Microsoft Learn)](https://learn.microsoft.com/dotnet/csharp/advanced-topics/expression-trees/expression-trees-execution#execution-and-lifetimes)

No further action warranted. The only path worth optimizing is the `ConstantExpression` branch, which already succeeds for all real-world SQL.

---

## Dead Code / Architectural Drift

### 11 `TableSchemaProvider` — revived with join-key metadata for Phase 2 JOINs

`TableSchemaProvider.cs` provides runtime column metadata for SymbolRecord, ChunkRecord, and RelationshipRecord via reflection. It is wired into DI and now includes `JoinKeyInfo` records that document foreign-key relationships between tables (`SymbolRecord.Id` ↔ `RelationshipRecord.SourceSymbolId`/`TargetSymbolId`, `ChunkRecord.SymbolId` ↔ `SymbolRecord.Id`, and the self-join pattern). `DescribeAll()` automatically includes join-key annotations in its output, and `DescribeJoinKeys()` exposes them separately for programmatic use.

---

## Testing Gaps

- ❌ No test for very large GROUP BY key cardinality (>10k groups)
- ❌ No concurrent SQL query execution test
- ❌ No benchmark baseline for query latency at various row counts
- ❌ No stress test against a store with >100K records

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
