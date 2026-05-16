# SQL Query — Known Issues & Limitations

| # | Issue | Effort | Impact | Status |
|---|-------|--------|--------|--------|
| 3a | CTEs (Common Table Expressions) | Medium | High | See [`SQL-CTE.md`](SQL-CTE.md) |
| 3b | JOINs (multi-table), UNION, subqueries | Large | High | |
| 6 | `materializeAsync` / `toAsyncEnumerable` deep reflection (~80 lines) | Medium | Medium | Won't Do — generic bridge applied |
| 9 | ORDER BY boxes via `GetValueOrDefault` | Small | Low | Won't Do — see note |
| 10 | `getConstantString` compiles throwaway expression | Small | Low | Won't Do — interpreted fallback applied |
| 11 | `TableSchemaProvider` not wired in | Small\* | Low | |

\* Wiring `TableSchemaProvider` itself is small; it is only needed when JOINs land (Large item).

---

## Parse / Syntax Gaps

### 3a CTEs (Common Table Expressions) — see [`SQL-CTE.md`](SQL-CTE.md)

CTEs were split from the broader JOINs/UNION/subqueries task. The parser already parses `WITH` clauses into `query.With.CteTables`. Requires implementing CTE materialization and CTE-aware table resolution. Low risk — pre-processing step with no changes to the core execution engine.

### 3b JOINs (multi-table), UNION, subqueries

These are structurally rejected (`SetExpression` check) or silently mishandled (joins would return cross-product data with no error). JOINs require deep architectural changes: multi-table FROM clause, qualified column names, cross-collection row merging, and the `TableSchemaProvider` (item 11) must be wired first.

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

### 11 `TableSchemaProvider` not wired in — will be needed for JOINs

`TableSchemaProvider.cs` provides runtime column metadata for SymbolRecord, ChunkRecord, and RelationshipRecord via reflection. It is commented out in `Program.cs` (DI registration) and in `SqlQueryTool.cs` (injection). Currently the schema info is inlined as static text in the tool's `[Description]` attribute.

This is fine today (single-table queries, static schema is sufficient). But when JOINs arrive, an LLM needs to know which columns are join-compatible across tables (e.g., `SymbolRecord.Id` ↔ `ChunkRecord.SymbolId`). That info must be generated dynamically or at least maintained alongside the record types — the inlined description will be a maintenance burden and will lack join-key metadata.

`TableSchemaProvider` should be revived as part of any JOINs feature and extended with join-key annotations or a foreign-key map.

---

## Testing Gaps

- ❌ No test for very large GROUP BY key cardinality (>10k groups)
- ❌ No concurrent SQL query execution test
- ❌ No benchmark baseline for query latency at various row counts
- ❌ No stress test against a store with >100K records
