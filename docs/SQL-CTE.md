# CTE (Common Table Expression) Implementation

## Analysis: Why CTEs Before JOINs

### Architectural Assessment

CTEs (`WITH name AS (query) SELECT ...`) and JOINs (`FROM t1 JOIN t2 ON ...`) were bundled as a single task (item 3 in `SQL-KNOWNISSUES.md`) but they differ fundamentally in scope, risk, and architectural impact.

| Dimension | CTEs | JOINs |
|-----------|------|-------|
| **Architecture impact** | Pre-processing step only | Deep — multi-table FROM, row merging, qualified columns |
| **Execution model** | Recursive call to existing `queryFilteredAsync` | New cross-collection query + row merge logic |
| **Row model changes** | None — CTE results are already `Dictionary<string, object?>` | Wide rows from N tables; needs `table.col` disambiguation |
| **Filter building** | Uses existing `SqlExpressionBuilder` (single record type) | Cross-type filter expressions; needs multi-type awareness |
| **Code isolation** | Self-contained — hooks into table resolution | Touches parser, filter builder, registry, projection, schema provider |
| **TableSchemaProvider needed?** | No — CTE columns come from the subquery's projection | Yes — LLM needs join-compatible column metadata |
| **Parser support** | Already parsed into `query.With` → `Sequence<CommonTableExpression>` | Already parsed into `TableWithJoins.Joins` → `Sequence<Join>` |
| **Risk** | **Low** — no changes to core execution engine | **High** — core architecture changes |

### Why CTEs Should Go First

1. **CTEs are a pre-processing step.** A CTE is syntactic sugar for a named subquery. The implementation evaluates each CTE query first, stores the results in memory, then substitutes references in the main query. **No fundamental changes** to the single-table execution pipeline are needed.

2. **CTE results slot into the existing row model.** CTEs produce `List<Dictionary<string, object?>>` — the same format the engine already uses. The main query treats a CTE reference as a virtual table whose data is already in memory. Filtering, ordering, grouping, and projection on CTE references work with zero changes to post-processing code.

3. **CTEs provide immediate value alone.** An MCP agent can express multi-step logic in a single SQL statement:
   ```sql
   WITH public_classes AS (
       SELECT * FROM SymbolRecord WHERE Modifiers LIKE '%public%'
   )
   SELECT Name, FilePath FROM public_classes ORDER BY Name
   ```

4. **CTEs reduce the surface area for JOINs later.** Once CTE table resolution exists, a derived table in FROM (`FROM (subquery) AS alias`) follows the same pattern — the only meaningful change needed for simple derived tables is handling `TableFactor.Derived` in `extractTableName`.

5. **You don't need JOINs to start seeing value from CTEs.** CTEs unlock complex queries independent of multi-table joins.

### Key Parser Types (SqlParserCS v0.6.5)

The parser already fully parses CTEs. No parser changes are needed.

```csharp
// Top-level query wrapper
public sealed record Query(
    SetExpression Body
) {
    public With? With { get; init; }    // ← THE WITH CLAUSE, currently ignored
    public OrderBy? OrderBy { get; set; }
    public Expression? Limit { get; init; }
    // ... Offset, Fetch, Locks, etc.
}

// WITH clause
public sealed record With(
    bool Recursive,
    Sequence<CommonTableExpression> CteTables
) : IWriteSql, IElement

// Single CTE definition
public sealed record CommonTableExpression(
    TableAlias Alias,           // The CTE name (e.g. "cte_name")
    Query Query,                // The CTE subquery body
    Ident? From = null,
    CteAsMaterialized? Materialized = null,
    bool IsExpression = false,
    bool IsReversed = false
) : IWriteSql, IElement

// Table alias
public sealed record TableAlias(
    Ident Name,                         // Alias identifier (string value)
    Sequence<TableAliasColumnDef>? Columns = null
) : IWriteSql, IElement
```

---

## Task Breakdown

### Suggested Execution Order

1. **Task 1**: CTE table registration — store CTE results in memory before main query
2. **Task 2**: CTE-aware table resolution — check CTE names in `extractTableName`
3. **Task 3**: Derived table (`FROM (subquery) AS alias`) support (optional, shares CTE infra)
4. **Task 4**: Tests — CTE queries end-to-end, nesting, shadowing, recursive guard
5. **Task 5**: Documentation — update `SQL-KNOWNISSUES.md`, add examples

### Coordination Notes

- Task 1 and Task 2 are tightly coupled — they should be done in sequence by one agent.
- Task 3 is independent after Task 2 (same table resolution hook, different `TableFactor`).
- Task 4 can run partially in parallel with Task 3 (basic CTE tests) but full coverage needs Task 3 first.
- No files outside `CodeMemory/SqlQuery/` (except tests) should be touched.
- The existing row pipeline (`applyOrderBy`, `applyGroupBy`, `projectRows`, etc.) needs **zero changes**.

---

## Task 1: CTE Execution — Materialize CTEs Before Main Query

### Priority

High

### Goal

Evaluate each CTE defined in `query.With` and store its results in memory so the main query can reference the CTE by name.

### Why this exists

The `query.With` property is populated by the parser but never accessed. CTE definitions must be recursively evaluated and their results cached before the main query executes.

### Scope

- Read `query.With.CteTables` if non-null
- For each `CommonTableExpression`:
  - If `Recursive` is true, return an error with a clear message ("Recursive CTEs not yet supported")
  - Recursively call the data-fetching path (`queryFilteredAsync` or `queryVectorAsync` depending on the CTE body) with `int.MaxValue` as the limit (CTE must produce all rows)
  - Store the result in a `Dictionary<string, List<Dictionary<string, object?>>>` keyed by `Alias.Name.Value`
- Pass this dictionary alongside the execution context so Task 2 can access it
- If a CTE references another CTE by name, resolve from the dictionary (already-evaluated CTEs)

### Constraints

- Only non-recursive CTEs are in scope. `WITH RECURSIVE` should return a clear error.
- CTE results must be fully materialized before the main query starts execution.
- CTE queries can have their own WHERE, ORDER BY, LIMIT — these execute within the CTE subquery.
- CTE alias must be unique (duplicate CTE names should return an error).
- A CTE can reference a previously-defined CTE within the same WITH clause (chained CTEs).

### Suggested implementation path

1. In `ExecuteAsync`, after parsing succeeds and before the main query execution, add a step to process `query.With`:

```csharp
Dictionary<string, List<Dictionary<string, object?>>>? cteResults = null;
if (query.With is not null)
{
    cteResults = await materializeCtesAsync(query.With, store, ct);
    // cteResults is now available for table resolution in the main query
}
```

2. `materializeCtesAsync` iterates CTEs in order, building a dictionary. Each CTE is evaluated by calling the same data-fetching path used for the main query (`queryFilteredAsync` / `queryVectorAsync`), passing `int.MaxValue` as the row limit.

3. When a CTE subquery's FROM clause references a previously-materialized CTE name, the data source switches from the store collection to the in-memory CTE results.

### Acceptance criteria

- `WITH cte AS (SELECT * FROM SymbolRecord WHERE Kind = 'Class') SELECT Name FROM cte` returns class names
- `WITH cte AS (SELECT Name, FilePath FROM SymbolRecord) SELECT * FROM cte WHERE Name LIKE '%Helper%'` returns filtered CTE results
- Chained CTEs work: `WITH a AS (...), b AS (SELECT * FROM a WHERE ...) SELECT * FROM b`
- `WITH RECURSIVE` returns a clear error
- Duplicate CTE names return an error
- Existing single-table queries without CTEs continue to work identically

### Files likely involved

- `src/CodeMemory/SqlQuery/SqlQueryService.cs`

---

## Task 2: CTE-Aware Table Resolution

### Priority

High

### Goal

When resolving a table name from the FROM clause, check CTE names before falling through to `CollectionRegistry`.

### Why this exists

Currently `extractTableName` resolves names against `CollectionRegistry` only. CTE references in the FROM clause would fail because the CTE name is not a registered collection.

### Scope

- Modify `extractTableName` (or the call site in `ExecuteAsync`) to accept the CTE results dictionary
- If the table name matches a CTE alias, skip the `CollectionRegistry` lookup and route data fetching to the in-memory CTE results
- If the table name is not a CTE, fall through to `CollectionRegistry` as before
- When a CTE is referenced, the main query's filter/ORDER BY/projection operates against the in-memory CTE rows (existing `Dictionary<string, object?>` format)

### Constraints

- The existing single-table path must be preserved unchanged for non-CTE queries.
- Column names in the main query reference the CTE's output columns (determined by the CTE's SELECT projection).
- No changes to `CollectionRegistry` are needed.

### Suggested implementation path

1. After collecting CTE results, change the table resolution code to check CTE names first:

```csharp
var tableName = extractTableName(selectBody.From[0].Relation);
if (tableName is null)
    return fail("Could not determine table name from FROM clause", sw);

bool isCte = cteResults?.ContainsKey(tableName) == true;
var entry = isCte ? null : registry.GetEntry(tableName);
if (!isCte && entry is null)
    return fail($"Unknown table '{tableName}'", sw);
```

2. For the data fetch: if `isCte`, skip `queryFilteredAsync`/`queryVectorAsync` and use the in-memory CTE results as the source. Apply the main query's WHERE filter over the CTE rows using the existing `buildFilterExpression` + in-memory filtering (similar to how HAVING filters grouped results).

### Acceptance criteria

- `WITH cte AS (...) SELECT * FROM cte WHERE ...` correctly applies the main query's WHERE filter on CTE rows
- CTE results appear as `Dictionary<string, object?>` rows compatible with ORDER BY, LIMIT, and projection
- Non-CTE queries are unaffected
- CTE alias takes priority over collection name (if a CTE has the same name as a registered table, the CTE wins)

### Files likely involved

- `src/CodeMemory/SqlQuery/SqlQueryService.cs`

---

## Task 3: Derived Table Support (`FROM (subquery) AS alias`)

### Priority

Medium

### Goal

Support derived tables (subqueries in FROM) using the same infrastructure as CTEs.

### Why this exists

`TableFactor.Derived` is currently rejected by `extractTableName` (returns `null`). A derived table is semantically identical to a CTE but defined inline rather than before the main query.

### Scope

- In `extractTableName`, add a branch for `TableFactor.Derived`:
  - Parse the derived table's `SubQuery` (a `Query` object)
  - Recursively execute the subquery (just like a CTE)
  - Use the `Alias.Name.Value` as the table name for column resolution
  - Return a special sentinel or table name
- At the call site, when a derived table is detected, use the materialized subquery results as the data source

### Constraints

- Derived table aliases must be present (required by SQL standard for subqueries in FROM).
- Nested derived tables should work (subquery within a subquery).
- The derived table's projection defines available columns in the outer query.

### Acceptance criteria

- `SELECT * FROM (SELECT Name, Kind FROM SymbolRecord WHERE Kind = 'Class') AS classes` returns correct rows
- `SELECT * FROM (SELECT * FROM (SELECT Name FROM SymbolRecord) AS inner) AS outer` (nested) works
- `SELECT * FROM (SELECT * FROM SymbolRecord) AS s WHERE s.Kind = 'Method'` works

### Files likely involved

- `src/CodeMemory/SqlQuery/SqlQueryService.cs`

---

## Task 4: Tests

### Priority

High

### Goal

Comprehensive test coverage for CTEs and derived tables.

### Why this exists

CTEs add a new execution path that runs before the main query. Every combination of CTE + existing features (WHERE, ORDER BY, GROUP BY, HAVING, LIMIT, DISTINCT, vector search) needs coverage.

### Scope

- Basic CTE with SELECT * FROM cte
- CTE with WHERE filter on the main query
- CTE with WHERE filter inside the CTE subquery
- CTE with ORDER BY
- CTE with GROUP BY / HAVING
- CTE with aggregate functions
- Chained CTEs (CTE referencing a prior CTE)
- CTE with LIMIT in the main query
- CTE with LIMIT inside the CTE subquery
- CTE name shadowing: CTE name equals collection name (CTE should win)
- Recursive CTE returns error
- Duplicate CTE name returns error
- Empty CTE (no rows in subquery)
- Derived table (subquery in FROM)
- Nested derived tables
- CTE followed by derived table in the same query
- Non-CTE queries must pass unchanged (regression)

### Constraints

- Tests use the same `InMemoryVectorStore` + `NgramEmbeddingGenerator` as existing tests.
- Test data should reuse the existing `seedSymbolsAsync` / `seedChunksAsync` helpers.
- Add new seed helpers if needed for multi-row scenarios.

### Acceptance criteria

- At least 20 new test methods covering the scenarios above.
- All existing 249 tests still pass.

### Files likely involved

- `src/CodeMemory.Tests/Services/Query/SqlQueryServiceTests.cs`

---

## Task 5: Documentation

### Priority

Low

### Goal

Update documentation to reflect CTE support and the new task split.

### Scope

- Update `docs/SQL-KNOWNISSUES.md`:
  - Split Task 3 into 3a (CTE — Won't Do) and 3b (JOINs — remaining)
  - Add reference to `docs/SQL-CTE.md`
- Add CTE examples to the `sql_query` MCP tool's description in `SqlQueryTool.cs` if appropriate

### Constraints

- Don't duplicate the full CTE analysis from this document.
- Keep the known issues table concise.

### Acceptance criteria

- `SQL-KNOWNISSUES.md` table reflects the split
- CTE syntax examples are documented where the MCP tool describes SQL syntax

### Files likely involved

- `docs/SQL-KNOWNISSUES.md`
- `src/CodeMemory/Mcp/SqlQueryTool.cs`

---

## Field-Testing Scenarios

Real-world scenarios enabled by CTEs. Each represents a coding task where CTEs make the query readable and composable.

### Prerequisites

- CTEs implemented per Tasks 1–2
- Derived tables implemented per Task 3 (for scenarios using `FROM (subquery) AS alias`)
- Indexing complete (`ping` returns `indexingCompleted: true`)

---

### Category 1: Multi-Step Analysis

Decompose complex questions into intermediate steps.

#### 1.1 Most complex public methods

```sql
WITH public_methods AS (
    SELECT Name, FilePath, LineStart, LineEnd
    FROM SymbolRecord
    WHERE Kind = 'Method' AND Modifiers LIKE '%public%'
)
SELECT Name, FilePath, (LineEnd - LineStart) AS Lines
FROM public_methods
ORDER BY Lines DESC LIMIT 10
```

**Why this tool:** Without CTEs, the filter (`Kind = 'Method' AND Modifiers LIKE '%public%'`) + expression (`LineEnd - LineStart`) + ORDER BY + LIMIT all sit in one flat query. The CTE separates the filtering step from the analysis step, making the intent clearer.

**Expect:** Top 10 longest public methods by line count.

---

#### 1.2 Enums and their usage locations

```sql
WITH enums AS (
    SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Enum'
)
SELECT Name, FilePath FROM enums
WHERE FilePath LIKE '%Domain%' OR FilePath LIKE '%Models%'
ORDER BY Name
```

**Expect:** Domain/model enums listed alphabetically. The CTE captures all enums; the main query scopes to specific directories.

---

#### 1.3 Files with both classes and interfaces

```sql
WITH class_files AS (
    SELECT DISTINCT FilePath FROM SymbolRecord WHERE Kind = 'Class'
),
interface_files AS (
    SELECT DISTINCT FilePath FROM SymbolRecord WHERE Kind = 'Interface'
)
SELECT FilePath FROM class_files
INTERSECT
SELECT FilePath FROM interface_files
ORDER BY FilePath
```

**Note:** This requires `SetExpression` support (UNION/INTERSECT/EXCEPT). If not implemented in the initial CTE pass, replace with equivalent chained CTE + WHERE IN approach (requires subquery support) or mark as post-MVP.

---

#### 1.4 Largest files by total symbol count

```sql
WITH symbol_counts AS (
    SELECT FilePath, COUNT(*) AS cnt
    FROM SymbolRecord
    GROUP BY FilePath
)
SELECT FilePath, cnt FROM symbol_counts
ORDER BY cnt DESC LIMIT 10
```

**Why this tool:** The CTE separates aggregation from ordering. Without CTEs, `GROUP BY` + `ORDER BY` must sit in one query with no intermediate naming.

**Expect:** Top 10 files by number of declared symbols.

---

### Category 2: Code Quality / Coverage Analysis

Answer questions about test coverage and code organization.

#### 2.1 Source classes missing tests

```sql
WITH source_classes AS (
    SELECT Name, FilePath FROM SymbolRecord
    WHERE Kind = 'Class' AND FilePath NOT LIKE '%Test%'
),
test_classes AS (
    SELECT Name, FilePath FROM SymbolRecord
    WHERE Kind = 'Class' AND FilePath LIKE '%Test%'
)
SELECT s.Name, s.FilePath FROM source_classes s
WHERE s.Name || 'Tests' NOT IN (SELECT Name FROM test_classes)
AND s.Name || 'Test' NOT IN (SELECT Name FROM test_classes)
ORDER BY s.Name
```

**Note:** String concat with `||` is supported. The `NOT IN (subquery)` requires subquery support — if not available, flatten with `FilePath LIKE '%'` patterns.

---

#### 2.2 Public API surface in a specific namespace

```sql
WITH public_api AS (
    SELECT Name, Kind, FilePath FROM SymbolRecord
    WHERE Modifiers LIKE '%public%'
      AND Kind IN ('Class', 'Interface', 'Record')
)
SELECT * FROM public_api
WHERE FilePath LIKE '%Services%'
ORDER BY Kind, Name
```

**Expect:** All public types in the Services directory, grouped by kind then sorted by name.

---

#### 2.3 Find classes with high method density

```sql
WITH class_info AS (
    SELECT Name, FilePath, LineStart, LineEnd FROM SymbolRecord
    WHERE Kind = 'Class'
),
method_counts AS (
    SELECT FilePath, COUNT(*) AS method_count FROM SymbolRecord
    WHERE Kind = 'Method'
    GROUP BY FilePath
)
SELECT c.Name, c.FilePath,
       (c.LineEnd - c.LineStart) AS class_size,
       m.method_count
FROM class_info c
JOIN method_counts m ON c.FilePath = m.FilePath
ORDER BY CAST(m.method_count AS REAL) / (c.LineEnd - c.LineStart) DESC
LIMIT 10
```

**Wait, JOINs aren't implemented.** Replace with a single-table approach — select classes ordered by method-to-size ratio computed from the CTE alone if the CTE groups on a broader dimension.

**Simplify to:**
```sql
WITH class_sizes AS (
    SELECT FilePath, (LineEnd - LineStart) AS Size
    FROM SymbolRecord WHERE Kind = 'Class'
)
SELECT FilePath, MAX(Size) AS MaxClassSize
FROM class_sizes
GROUP BY FilePath
ORDER BY MaxClassSize DESC LIMIT 10
```

**Expect:** Top 10 files by largest single class definition.

---

### Category 3: Architecture Exploration

Use CTEs to break down architecture questions that span multiple dimensions.

#### 3.1 Find all async methods and their containing files

```sql
WITH async_methods AS (
    SELECT Name, FilePath FROM SymbolRecord
    WHERE Kind = 'Method' AND Modifiers LIKE '%async%'
)
SELECT FilePath, COUNT(*) AS async_count
FROM async_methods
GROUP BY FilePath
ORDER BY async_count DESC LIMIT 10
```

**Why this tool:** The CTE first filters for async methods, then the main query aggregates by file. Without CTEs, the GROUP BY would apply to all methods and the async filter would need to be in a HAVING clause.

**Expect:** Top 10 files by number of async methods.

---

#### 3.2 Chained CTE: public interfaces and their implementing classes

```sql
WITH public_interfaces AS (
    SELECT Name, FilePath FROM SymbolRecord
    WHERE Kind = 'Interface' AND Modifiers LIKE '%public%'
),
public_classes AS (
    SELECT Name, FilePath FROM SymbolRecord
    WHERE Kind = 'Class' AND Modifiers LIKE '%public%'
)
SELECT Name, FilePath FROM public_interfaces
ORDER BY Name
```

**Expect:** Both CTEs are materialized independently. The main query selects from the first — demonstrates chaining works.

---

#### 3.3 Vector search with CTE (pre-filter chunks by language)

```sql
WITH csharp_chunks AS (
    SELECT Id, SymbolId, FilePath, Content, Embedding
    FROM ChunkRecord
    WHERE Language = 'CSharp'
)
SELECT FilePath, Content FROM csharp_chunks
WHERE Content LIKE '%async%'
ORDER BY Similarity DESC LIMIT 5
```

**Why this tool:** Combines CTE pre-filtering with vector search. The CTE reduces the search space to C# chunks, then `ORDER BY Similarity DESC` ranks by embedding similarity. Without CTEs, all the filtering and ranking must sit in one flat WHERE.

**Expect:** Top 5 C# chunks about async, ranked by semantic similarity. Each row includes `__score`.

---

### Category 4: Derived Table Scenarios (Task 3)

If derived tables are also implemented, these scenarios become available.

#### 4.1 Inline subquery as data source

```sql
SELECT * FROM (
    SELECT Name, Kind, FilePath
    FROM SymbolRecord
    WHERE Kind IN ('Class', 'Interface')
    ORDER BY Name
) AS types
WHERE FilePath LIKE '%Core%'
```

**Expect:** Core module types, pre-sorted and filtered.

---

#### 4.2 Nested subqueries

```sql
SELECT Name FROM (
    SELECT * FROM (
        SELECT Name, Kind FROM SymbolRecord
        WHERE Modifiers LIKE '%public%'
    ) AS public_symbols
    WHERE Kind = 'Method'
) AS public_methods
ORDER BY Name
```

**Expect:** All public method names, sorted.

---

#### 4.3 Mixed CTE + derived table

```sql
WITH class_names AS (
    SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Class'
)
SELECT * FROM (
    SELECT Name, FilePath FROM class_names WHERE FilePath LIKE '%Service%'
) AS service_classes
WHERE Name LIKE '%Handler%'
ORDER BY Name
```

**Expect:** Classes matching both the CTE filter (Class kind), the inner filter (Service path), and the outer filter (Handler name).

---

### Category 5: Edge Cases & Error Handling

Test these to understand CTE-specific boundaries.

#### 5.1 CTE name shadows collection name

```sql
WITH SymbolRecord AS (
    SELECT Name, Kind FROM SymbolRecord WHERE Kind = 'Interface'
)
SELECT * FROM SymbolRecord
```

**Expect:** Only interfaces returned, not all SymbolRecord data. The CTE name takes priority over the collection.

---

#### 5.2 Chained CTEs with dependency

```sql
WITH step1 AS (
    SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Class'
),
step2 AS (
    SELECT Name, FilePath FROM step1 WHERE FilePath LIKE '%Core%'
)
SELECT * FROM step2 ORDER BY Name
```

**Expect:** Classes in Core directory, ordered by name. `step2` references `step1` which is evaluated first.

---

#### 5.3 Duplicate CTE names

```sql
WITH a AS (SELECT * FROM SymbolRecord LIMIT 1),
     a AS (SELECT * FROM SymbolRecord LIMIT 2)
SELECT * FROM a
```

**Expect:** Error about duplicate CTE name.

---

#### 5.4 Recursive CTE

```sql
WITH RECURSIVE nums AS (
    SELECT 1 AS n
    UNION ALL
    SELECT n + 1 FROM nums WHERE n < 10
)
SELECT * FROM nums
```

**Expect:** Clear error: "Recursive CTEs not yet supported".

---

#### 5.5 CTE referencing undefined CTE

```sql
WITH a AS (SELECT * FROM b)
SELECT * FROM a
```

**Expect:** Error about unknown table `b` — the CTE `a` references `b` which doesn't exist as a CTE or collection.

---

#### 5.6 Empty CTE

```sql
WITH empty_cte AS (
    SELECT * FROM SymbolRecord WHERE Kind = 'NonExistent'
)
SELECT COUNT(*) FROM empty_cte
```

**Expect:** 0 rows (or 0 for COUNT). No crash on empty CTE.

---

#### 5.7 CTE with all post-processing steps

```sql
WITH filtered AS (
    SELECT FilePath, Kind, Name FROM SymbolRecord
    WHERE Modifiers LIKE '%public%'
)
SELECT Kind, COUNT(*) AS cnt
FROM filtered
GROUP BY Kind
HAVING cnt > 1
ORDER BY cnt DESC
```

**Expect:** Symbol kinds with more than 1 public member, ordered by count descending. Tests that GROUP BY, HAVING, and ORDER BY all compose correctly over CTE results.

---

### Known Limitations for CTE Queries

| Construct | Status | Note |
|-----------|--------|------|
| `WITH RECURSIVE` | Not supported | Returns clear error |
| Duplicate CTE names | Error | CTE aliases must be unique |
| CTE referencing undefined CTE | Error | Forward references not allowed; CTEs evaluated in order |
| Cyclic CTE references | Error | CTE must not reference itself (without RECURSIVE) |
| Subqueries in WHERE/IN | Not supported | CTE materializes the result set, but WHERE IN (subquery) requires separate subquery support |
| Derived tables (`FROM (subquery)`) | Requires Task 3 | Shares CTE infrastructure |
| CTE with UNION/INTERSECT | Not supported | Requires `SetExpression.SetOperation` support |
| Non-recursive CTEs | Supported | Primary feature |
| Chained CTEs | Supported | Later CTEs can reference earlier ones |
| CTE shadowing collection names | Supported | CTE alias wins over CollectionRegistry |

### How to Run These

Through your MCP client, call:

```json
{
  "name": "sql_query",
  "arguments": {
    "query": "WITH public_methods AS (SELECT Name, FilePath, LineStart, LineEnd FROM SymbolRecord WHERE Kind = 'Method' AND Modifiers LIKE '%public%') SELECT Name, (LineEnd - LineStart) AS Lines FROM public_methods ORDER BY Lines DESC LIMIT 10",
    "maxResults": 20
  }
}
```
