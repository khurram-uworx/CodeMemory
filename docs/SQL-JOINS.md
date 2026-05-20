# SQL JOINs — Implementation & Phase 2 Plan

## What Phase 1 Implemented

Phase 1 added **in-memory cross-join + filter** support for multi-table queries. This covers:

| Feature | Status |
|---------|--------|
| Comma-separated FROM (`FROM t1, t2`) | Done |
| Explicit JOIN syntax (`FROM t1 JOIN t2 ON ...`) | Done |
| Self-joins (`FROM SymbolRecord c, SymbolRecord m`) | Done |
| INNER JOIN, LEFT JOIN, CROSS JOIN (parsed, ON applied) | Done |
| CTE + JOIN composition | Done |
| Qualified column names (`s.Name`, `r.TargetSymbolId`) | Done |
| GROUP BY / ORDER BY / aggregates on merged results | Done |

## Phase 2 — Not Yet Implemented

### 1. Proper INNER JOIN Optimization

**Current:** Computes full cartesian product of all tables, then filters by ON+WHERE. For 1000×100 rows, that's 100K intermediate rows.

**Target:** `INNER JOIN ... ON` should iterate the left table and look up matching right rows by evaluating the ON condition per left row, avoiding the full cross-product. This is O(leftRows × rightRows) in the worst case anyway (in-memory hash-join requires a hashable join key, which is complex with arbitrary expressions).

**Effort:** Medium — restructure `executeJoinQueryAsync` to apply ON condition during the merge loop in `cartesianMerge` instead of after.

### 2. LEFT / RIGHT / FULL OUTER JOIN Semantics

**Current:** All joins are treated as inner joins — unmatched rows are discarded.

**Target:**
- `LEFT JOIN`: preserve all left rows, fill NULL for right columns when no match
- `RIGHT JOIN`: mirror of LEFT
- `FULL OUTER JOIN`: preserve both sides

**Key changes needed:**
- `cartesianMerge` needs to know the join type for each pair of tables
- When no ON match is found for a left row, emit the row with NULLs for right columns
- NULL handling for all post-processing (GROUP BY treats NULL as a group key, ORDER BY sorts NULLs, etc.)

**Effort:** Large — requires refactoring the merge pipeline to be join-type-aware, and adding NULL-value generation for missing right-side columns.

### 3. `USING(col)` Shorthand

**Current:** `JOIN ... USING(col)` is parsed but the `JoinConstraint.Using` variant is ignored (no error, no effect).

**Target:** `USING(col1, col2)` should automatically emit `ON left.col1 = right.col1 AND left.col2 = right.col2`.

**Key changes needed:**
- Extend `mergeOnConditions` to handle `JoinConstraint.Using` by generating the equality expressions
- Need to resolve the column to the correct left/right table (trivial when column names are unique, requires qualified names when ambiguous)

**Effort:** Small — ~10 lines in `mergeOnConditions`.

### 4. Nested Joins (Parenthesized Joins)

**Current:** `TableFactor.NestedJoin` is silently ignored.

**Target:** Support `FROM (t1 JOIN t2 ON ...) JOIN t3 ON ...` — parenthesized join groups.

**Key changes needed:**
- Extend `parseFromClause` to handle `NestedJoin` — it contains a `TableWithJoins` which is recursive
- The sub-join must be merged first, then its result treated as a derived table

**Effort:** Medium — recursive structure, but the merge infrastructure already exists.

### 5. WHERE Subqueries (`WHERE col IN (SELECT ...)`)

**Current:** Rejected by the `SetExpression` check at the top of `ExecuteAsync`.

**Target:** Support `WHERE col IN (SELECT ... FROM ...)`. The subquery result is a list of scalar values; `IN` checks membership.

**Key changes needed:**
- Remove the blanket `SetExpression.SelectExpression` check at the query level
- Instead, check at the `From` level (main FROM must still be a simple SELECT, but WHERE can contain subqueries)
- Execute the subquery and materialize the results to evaluate the IN expression
- Modify `evaluateExpression` to handle subquery expressions

**Effort:** Large — requires recursive query execution for subqueries, result caching, and value-list extraction.

### 6. UNION / INTERSECT / EXCEPT

**Current:** Rejected by the `SetExpression` check.

**Target:** `SELECT ... UNION [ALL] SELECT ...` — row-wise set operations.

**Key changes needed:**
- Execute both sides independently
- For UNION ALL: concatenate results
- For UNION: concatenate + deduplicate
- For INTERSECT: keep rows in both
- For EXCEPT: keep rows only in left

**Effort:** Medium — the individual query execution already works; the main effort is result merging and deduplication.

### 7. `TableSchemaProvider` Join-Key Metadata

**Current:** `TableSchemaProvider` is wired but provides only basic column info.

**Target:** Annotate join-compatible columns (foreign-key relationships between tables):
- `SymbolRecord.Id` ↔ `RelationshipRecord.SourceSymbolId` / `TargetSymbolId`
- `ChunkRecord.SymbolId` ↔ `SymbolRecord.Id`
- `SymbolRecord.FullName` ↔ prefix of `SymbolRecord.FullName` (self-join parent-child)

**Effort:** Small — add a `JoinKeyInfo` record type and register known join pairs in `TableSchemaProvider`. Wire the annotations into the MCP tool `[Description]` so the LLM can discover join paths.

## Implementation Priority for Phase 2

| Priority | Item | Effort | Impact | Why |
|----------|------|--------|--------|-----|
| P0 | OUTER JOIN semantics | Large | High | LEFT JOIN is a common pattern; current silent inner-only behavior is wrong |
| P1 | WHERE subqueries | Large | High | Enables correlated subqueries and `IN (SELECT)` which agents frequently attempt |
| P2 | JOIN optimization | Medium | Medium | Performance gain for large datasets (not critical at repo scale) |
| P3 | `USING(col)` | Small | Medium | Low effort, fills a parsing gap |
| P4 | Join-key metadata | Small | Medium | Helps LLMs discover join paths; complements other features |
| P5 | Nested joins | Medium | Low | Rare in practice for repo queries |
| P6 | UNION | Medium | Low | Rare at repo scale; CTEs + multiple queries are usually sufficient |

## Technical Notes

### Current Architecture (Phase 1)

```
ExecuteAsync()
  └─ detectMultiTable() ──true──▶ parseFromClause() → TableRef[]
       │                             mergeOnConditions() → ON + WHERE
       │                             executeJoinQueryAsync()
       │                               ├─ fetch each table's rows
       │                               ├─ prefixRows() → alias.col keys
       │                               ├─ cartesianMerge() → cross-product
       │                               └─ filterCteRows(mergedWhere) → filter
       └─ false ──▶ single-table dispatch (existing)
  └─ GROUP BY / ORDER BY / LIMIT / project (shared)
```

### Phase 2 Target Architecture

```
ExecuteAsync()
  └─ detectMultiTable() ──true──▶ parseFromClause() → TableRef[]
       │                             mergeOnConditions() → ON + WHERE
       │                             executeJoinOptimizedAsync()
       │                               ├─ fetch each table's rows
       │                               ├─ prefixRows()
       │                               ├─ mergeTablesWithJoinTypes()
       │                               │    └─ INNER: filter-while-merging
       │                               │    └─ LEFT:  preserve-unmatched
       │                               │    └─ CROSS: full cartesian
       │                               └─ filterCteRows(remainingWhere)
       └─ false ──▶ single-table dispatch
  └─ GROUP BY / ORDER BY / LIMIT / project (shared)
```

### Key Files

| File | Phase 1 Changes | Phase 2 Changes Likely |
|------|-----------------|----------------------|
| `src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs` | New: `TableRef`, `detectMultiTable`, `parseFromClause`, `mergeOnConditions`, `prefixRows`, `cartesianMerge`, `executeJoinQueryAsync`, `rowGetValue`. Modified: `ExecuteAsync`, `applyGroupBy`, `makeSortSelector`, `evaluateExpression` | Refactor merge pipeline for join-type awareness, add NULL generation, add subquery execution |
| `src/CodeMemory.Mcp/SqlQuery/TableSchemaProvider.cs` | Wired in | Add join-key metadata annotations |
| `src/CodeMemory.Mcp/Tools/SqlQueryTool.cs` | Wired schema provider, updated `[Description]` | Dynamic schema generation for LLM |
| `docs/SQL-KNOWNISSUES.md` | Updated status | Update as features land |
