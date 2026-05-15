# SQL Query — Known Issues & Limitations

| # | Issue | Effort | Impact |
|---|-------|--------|--------|
| 1 | `ORDER BY` ambiguous column → first match, no warning | Small | Low |
| 2 | `DISTINCT` silently ignores computed expressions | Small | Low |
| 3 | Subqueries, CTEs, UNION, JOIN not implemented | Large | High |
| 4 | `CASE` / `COALESCE` / `CAST` silently return null | Medium | Medium |
| 5 | `LIMIT 1` w/o `ORDER BY` is non-deterministic | Small | Low |
| 6 | `materializeAsync` / `toAsyncEnumerable` deep reflection (~80 lines) | Medium | Medium |
| 7 | Mixed-type comparisons in ORDER BY/HAVING use `Convert.ToDouble` | Small | Low |
| 8 | `COUNT(column)` includes empty strings | Small | Low |
| 9 | ORDER BY boxes via `GetValueOrDefault` | Small | Low |
| 10 | `getConstantString` compiles throwaway expression | — | — |
| 11 | No test >10k GROUP BY groups | Medium | Low |
| 12 | No concurrent SQL query test | Medium | Medium |
| 13 | No benchmark baseline | Medium | Medium |
| 14 | No stress test >100K records | Medium | Low |
| 15 | `TableSchemaProvider` not wired in | Small\* | Low |

\* Wiring `TableSchemaProvider` itself is small; it is only needed when JOINs land (Large item).

---



## Semantic / Silent-Failure Gaps

### `ORDER BY` with ambiguous column name resolves to first match

If a `SELECT` list has two columns with the same base name (e.g. `SELECT Kind AS Kind, COUNT(*) AS Kind`), `resolveSortColumn` returns the first match only. No warning is emitted.

### `DISTINCT` silently ignores computed expressions

`DISTINCT` operates on raw row column names before projection. Computed expressions (e.g. `SELECT LineEnd - LineStart`) have no raw column to compare against, so rows collapse to one. Computed expressions with aliases (e.g. `SELECT LineEnd - LineStart AS Length`) are also not considered in DISTINCT because the alias does not exist in the raw row.

---

## Parse / Syntax Gaps

### Subqueries, CTEs, UNION, JOIN are not implemented

These are structurally rejected (`SetExpression` check) or silently mishandled (joins would return cross-product data with no error).

### `CASE`, `COALESCE`, `CAST` silently return null

These parse successfully but `evaluateExpression` has no handler — returns `null`. The query succeeds with null values in the result column rather than producing an error.

---

## Runtime / Execution Gaps

### Non-deterministic `LIMIT 1` without ORDER BY

`SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 1` returns an arbitrary row because InMemoryVectorStore has no natural order. This is correct SQL behavior but may surprise LLMs expecting deterministic output.

### `materializeAsync` / `toAsyncEnumerable` use deep reflection to enumerate `IAsyncEnumerable<T>`

Both methods manually invoke `IAsyncEnumerable<T>` via `MethodInfo.Invoke` — `GetAsyncEnumerator`, `MoveNextAsync`, `GetAwaiter`/`GetResult`, `DisposeAsync` — instead of using `await foreach`. This is ~80 lines of fragile reflection duplicated across two methods.

**Root cause:** The generic type argument is erased at this call site because `GetAsync` and `SearchAsync` are found via reflection. The return type is `object`, so `await foreach` cannot be used directly.

**Risk:** Any change to the `IAsyncEnumerable<T>` or `ValueTask` API surface silently breaks at runtime.

---

## Type / Conversion Gaps

### Mixed-type comparisons in ORDER BY / HAVING use `Convert.ToDouble`

If a column stores both `int` and `string` values, comparison converts both to `double`. Strings like `"abc"` would throw at runtime. This matches the existing HAVING evaluator behavior.

### `COUNT(column)` includes zero-length strings

`COUNT(col)` excludes SQL NULLs but does not exclude empty strings `""`. If the semantics of "null" vs. "empty" matter for your aggregation, ensure the data model uses `null` rather than `""`.

---

## Performance

### ORDER BY boxes all values via `GetValueOrDefault`

`applyOrderBy` uses `r.GetValueOrDefault(sortColumn)` which returns `object?`, boxing every value type for comparison against the sort key. Acceptable at repo scale but doubles allocation pressure on sorted columns.

### `getConstantString` compiles a throwaway expression per LIKE pattern

`SqlExpressionBuilder.cs:94` falls through to `LinqExpr.Lambda<Func<string>>(expr).Compile()()` when the LIKE pattern is not a raw `ConstantExpression`. This compiles a new delegate on every LIKE/ILIKE evaluation.

In practice, the LIKE/ILIKE pattern expression is always a new tree (different pattern text each call), so caching would not help. The only case to optimize is the first branch in `getConstantString` which checks for `ConstantExpression` directly — this succeeds 100% of the time for patterns that are string literals in SQL. The fallback `Compile()` path only triggers if the pattern expression is a computed expression (extremely rare in real-world SQL). **No action warranted.**

---

## Testing Gaps

- ❌ No test for very large GROUP BY key cardinality (>10k groups)
- ❌ No concurrent SQL query execution test
- ❌ No benchmark baseline for query latency at various row counts
- ❌ No stress test against a store with >100K records

---

## Dead Code / Architectural Drift

### `TableSchemaProvider` not wired in — will be needed for JOINs

`TableSchemaProvider.cs` provides runtime column metadata for SymbolRecord, ChunkRecord, and RelationshipRecord via reflection. It is commented out in `Program.cs` (DI registration) and in `SqlQueryTool.cs` (injection). Currently the schema info is inlined as static text in the tool's `[Description]` attribute.

This is fine today (single-table queries, static schema is sufficient). But when JOINs arrive, an LLM needs to know which columns are join-compatible across tables (e.g., `SymbolRecord.Id` ↔ `ChunkRecord.SymbolId`). That info must be generated dynamically or at least maintained alongside the record types — the inlined description will be a maintenance burden and will lack join-key metadata.

`TableSchemaProvider` should be revived as part of any JOINs feature and extended with join-key annotations or a foreign-key map.
