# SQL Query — Known Issues & Limitations

## Semantic / Silent-Failure Gaps

### ORDER BY cannot reference computed expression columns (non-GROUP BY)

`SELECT LineEnd - LineStart AS Length FROM SymbolRecord ORDER BY Length` sorts by the raw `Length` column (which does not exist in the record) rather than the computed expression. All rows compare as `null`, producing effectively random ordering.

**Root cause:** ORDER BY runs before `projectRows` (which evaluates expressions). For GROUP BY queries, aliases are materialized before ORDER BY, so the alias *is* available.

**Workaround:** In non-GROUP BY queries, ORDER BY by an existing record column, not a computed alias.

### `ORDER BY` with ambiguous column name resolves to first match

If a `SELECT` list has two columns with the same base name (e.g. `SELECT Kind AS Kind, COUNT(*) AS Kind`), `resolveSortColumn` returns the first match only. No warning is emitted.

### `DISTINCT` does not account for projection-only columns

`DISTINCT` is computed on the full raw row before `projectRows` strips it down. If two rows differ only in columns excluded from the SELECT list, they produce distinct results even though the projection looks identical.

---

## Parse / Syntax Gaps

### No trailing semicolons inside compound statements

The parser rejects multiple statements. A single trailing semicolon on a `SELECT` is fine (`SELECT * FROM SymbolRecord;`). Two statements (`SELECT 1; SELECT 2`) returns a clear error.

### String concatenation not supported

`SELECT 'foo' || 'bar'` is parsed as a `BinaryOp` with `StringConcat` operator but returns `null` from `evaluateExpression` — no error, no result.

### Subqueries, CTEs, UNION, JOIN are not implemented

These are structurally rejected (`SetExpression` check) or silently mishandled (joins would return cross-product data with no error).

###  Comments, `CASE`, `COALESCE`, `CAST` not supported

May produce a parse error or silently drop the expression.

---

## Runtime / Execution Gaps

### Generic catch-all loses SQL context on unexpected errors

The `try` block at `SqlQueryService.cs:862` wraps all unexpected exceptions with `"Execution error: {ex.Message}"`. A `NullReferenceException` would return only `"Object reference not set to an instance of an object"` with no indication of which SQL or stage failed.

### `AstExpr.Nested` not handled in WHERE filter builder

`SqlExpressionBuilder.visit()` does not have a case for `AstExpr.Nested`. Parenthesized WHERE conditions like `WHERE (Kind = 'Class')` would currently throw. This is because the parser *should* flatten parenthetical grouping, but if it ever emits a `Nested` node in a WHERE context, it will fail.

### Non-deterministic `LIMIT 1` without ORDER BY

`SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 1` returns an arbitrary row because InMemoryVectorStore has no natural order. This is correct SQL behavior but may surprise LLMs expecting deterministic output.

---

## Type / Conversion Gaps

### Mixed-type comparisons in ORDER BY / HAVING use `Convert.ToDouble`

If a column stores both `int` and `string` values, comparison converts both to `double`. Strings like `"abc"` would throw at runtime. This matches the existing HAVING evaluator behavior.

### `COUNT(column)` includes zero-length strings

`COUNT(col)` excludes SQL NULLs but does not exclude empty strings `""`. If the semantics of "null" vs. "empty" matter for your aggregation, ensure the data model uses `null` rather than `""`.

---

## Testing Gaps

- No test for `LIMIT 0`
- No test for very large GROUP BY key cardinality (>10k groups)
- No test for `WHERE` expressions containing `AstExpr.Nested`
- No test for ORDER BY on a column that exists in the record but is not in the SELECT list (should work but not verified)

---

## Dead Code / Architectural Drift

### `TableSchemaProvider` not wired in — will be needed for JOINs

`TableSchemaProvider.cs` provides runtime column metadata for SymbolRecord, ChunkRecord, and RelationshipRecord via reflection. It is commented out in `Program.cs` (DI registration) and in `SqlQueryTool.cs` (injection). Currently the schema info is inlined as static text in the tool's `[Description]` attribute.

This is fine today (single-table queries, static schema is sufficient). But when JOINs arrive, an LLM needs to know which columns are join-compatible across tables (e.g., `SymbolRecord.Id` ↔ `ChunkRecord.SymbolId`). That info must be generated dynamically or at least maintained alongside the record types — the inlined description will be a maintenance burden and will lack join-key metadata.

`TableSchemaProvider` should be revived as part of any JOINs feature and extended with join-key annotations or a foreign-key map.

### `SqlQueryResult` defined inline rather than a separate file

The plan called for a dedicated `SqlQueryResult.cs` file. The record is defined inside `SqlQueryService.cs` at line 16. This is a minor organizational deviation with no functional impact.
