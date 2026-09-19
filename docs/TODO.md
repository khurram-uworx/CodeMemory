# Plan: Fix #122 — schema-validated unknown-column diagnostics for `sql_query`

## Problem

`sql_query` (Mcp host, in-memory vector-store engine) reports unknown column names poorly or not
at all:

- **Repro from the issue** `WHERE Path LIKE '%Kgs%'` (bad column `Path`; real column is
  `FilePath`): historically produced `Parse error: Expected a SQL statement, found Name, Line: 1,
  Col: 14` — a cryptic grammar error pointing at the wrong token.
- Gold standard (issue's own ask): `Unknown column 'Path'. Available columns: Id, Name, Kind,
  FilePath, LineStart, LineEnd, FullName, Modifiers, Documentation, IsPublic, IsStatic, IsSealed`.

## Current-state analysis (verified by live probes)

1. **The parse error no longer reproduces.** `SqlParserCS` (sqlparsercs) **0.6.5** — the pinned
   version — parses the exact repro query (and single-line/multi-line variants) cleanly into one
   `Statement.Select`. The old "Expected a SQL statement" error is thrown by the library's
   *statement-boundary* path (`Parser.Statements.cs:35/120` — fires when the first token of a
   statement isn't a keyword), i.e. a syntactic artifact of an older build, not a column-name
   rejection. Column-name problems are semantic; the grammar never rejects them.
2. **The WHERE error is already "clear", but not ideal.** `SqlExpressionBuilder.resolveProperty`
   throws `InvalidOperationException("Column 'Path' not found on type 'SymbolRecord'. Available
   columns: …")`, caught by `SqlQueryTool` → `{ success:false, error:"Query execution failed:
   …" }`. Good list, but: internal "type 'SymbolRecord'" phrasing, an exception path (not the
   `fail()` sentinel used everywhere else), reflection-derived (not `TableSchemaProvider`
   schema), and it only fires for the single real-table WHERE case.
3. **Unknown-column handling per clause today (Mcp host):**

| Clause | Today at HEAD | Verdict |
|---|---|---|
| `WHERE` (single real table) | `InvalidOperationException` → "Column 'X' not found on type 'T'. Available columns: …" | clear, but internal phrasing / exception / reflection |
| `SELECT` projection (single real table, `SqlQueryService.cs:2242-2259`, from #120) | `fail()` "Column 'X' not found on 'SymbolRecord'. Available columns: …" (incl. aggregate args) | clear, runs *after* execution |
| `ORDER BY` | `rowGetValue` → `null` → sorts as if all equal | **silent** |
| `GROUP BY` | `rowGetValue` → `null` → single bucket | **silent** |
| `HAVING` | `TryGetValue` → `null` → rows filtered out | **silent** |
| `JOIN ON` / multi-table `WHERE` | `evaluateExpression` → `GetValueOrDefault` → `null` (rows are alias-prefixed, so bare keys never match) | **silent (empty result)** |
| Multi-table / CTE projection | unvalidated → missing keys silently dropped in `projectRows` | **silent (`{}`-ish rows)** |
| Parser errors | `ParseErrorFormatter` (both hosts): position + caret + sanitized (from #120) | good, except `Line==0` cases have no caret, and `;`-split statements give cryptic "Expected a SQL statement" |

4. **Parser-error residual shapes** (confirmed on 0.6.5):
   - No position at all (`Line==0`): `SELECT Name, FROM SymbolRecord` →
     `Expected Expected an expression, found: Identifier { Ident = FROM }` (sanitized to
     `Expected an expression, found: identifier 'FROM'`, but no line/col/caret because the
     formatter's `Line: > 0` gate fails).
   - Stray `;` inside a query → `Expected a SQL statement, found Path, Line: 2, Col: 7` (position
     correct, no hint about the semicolon).

## Proposed changes

**A. Schema-first validation pass (Mcp host).** New static helper
`src/CodeMemory.Mcp/SqlQuery/SqlQueryValidator.cs` that walks the parsed AST identifiers and
validates them against the authoritative table schemas from `TableSchemaProvider` (the same
source `DESCRIBE`/`PRAGMA table_info` use). Design:

- Entry point (sketch):
  ```csharp
  // after table resolution, before any execution — fail fast, never silent
  string? error = SqlQueryValidator.Validate(body, query.OrderBy, validatorContext);
  if (error is not null) return fail(error, sw);
  ```
- Covers identifiers in: `SELECT` projection (explicit columns + aggregate args), `WHERE`
  (recurse through `Like`/`ILike`/`InList`/`Between`/`IsNull`/`IsNotNull`/`Nested`/`UnaryOp`/
  `BinaryOp`), `GROUP BY`, `HAVING`, `ORDER BY`, `JOIN ON` — the expression-node walk mirrors
  `SqlExpressionBuilder.visit`.
- Resolution semantics:
  - Single real table: unqualified `col` → must be in that table's schema; qualified
    `t.col` → column must be in that table's schema (qualifier name ignored, matching today's
    `CompoundIdentifier` resolution).
  - Multi-table: qualified `alias.col` → must resolve to the FROM alias and that table's schema;
    **unqualified → error with guidance** ("qualify with table alias") — see Decision 2.
  - CTE / derived tables: columns from the CTE's/derived query's projected result keys; recurse
    into `InSubquery`, derived `FROM` subqueries, and CTE bodies.
  - `ORDER BY`: allow numeric positions, projection aliases/names, `Similarity` (vector-search
    virtual column → `__score`), then real columns.
  - `HAVING`: identifiers resolve against the projected column names/aliases/aggregate keys
    (grouped rows are keyed that way), then real columns.
- Not validated: `SELECT *`, `COUNT(*)`, `__`-prefixed internals, literals.
- Message format (per issue's ask, plus table context):
  - `Unknown column '{col}'. Available columns: {sorted, comma-separated}.`
  - Qualified: `Unknown column '{table}.{col}'. Available columns: {sorted}.`
  - Multi-table unqualified: `Unknown column '{col}' — qualify with a table alias ({aliases}). Available columns: {union}.`
- **Replace** the reflection-based projection check at `SqlQueryService.cs:2242-2259` and the
  `resolveProperty` message with the schema-validated `fail()` sentinel; keep
  `SqlExpressionBuilder.resolveProperty` as an internal safety net (should no longer surface).

**B. ParseErrorFormatter honesty (both hosts: `CodeMemory.Mcp/SqlQuery/ParseErrorFormatter.cs`
and `CodeMemory.AspNet/Tools/ParseErrorFormatter.cs`).**

- When the exception carries no position (`Line == 0`) but the sanitized message names the token
  (e.g. `found: identifier 'FROM'`), search the SQL text for that token and emit a positioned
  caret when the token is found exactly once (conservative fallback; never worse than today).
- When the message is "Expected a SQL statement, found X" and the SQL contains `;`, append:
  ` — remove the ';' (single-statement queries only)`.

**C. Tests** (NUnit, existing conventions; `createServices()` + `seedSymbolsAsync()` in
`SqlQueryServiceTests`; `AspNetSqlQueryToolTests` for the formatter):

- Exact repro: `Where_UnknownColumn_ReturnsSchemaValidatedError` — asserts `Unknown column
  'Path'`, `Available columns:`, contains `FilePath`, and **not** `Parse error`.
- One per silent clause: `OrderBy_UnknownColumn_ReturnsClearError`,
  `GroupBy_UnknownColumn_ReturnsClearError`, `Having_UnknownColumn_ReturnsClearError`,
  `JoinOn_UnknownColumn_ReturnsClearError`, `Select_UnknownColumn_MultiTableJoin_ReturnsClearError`.
- Guards (no regression): `OrderBy_AliasAndNumeric_StillSucceeds`,
  `OrderBy_Similarity_VectorSearch_StillSucceeds`, `Select_ComputedExpression_StillSucceeds`
  (e.g. `LineEnd - LineStart AS Length`), existing `Select_UnknownColumn_*` / `ParseError_*`
  tests updated only where the message contract deliberately changed.
- `ParseErrorFormatter_NoLocation_FindsTokenPosition`, `ParseError_StraySemicolon_Hint`.

**D. Docs.** AGENTS.md §SQL Parser Development Reference: add a note that `sql_query` validates
identifiers against the table schema before execution (schema-first diagnostics), matching the
issue's recommended strategy.

## Verification

1. `dotnet build` — solution compiles.
2. `dotnet test` — full suite + new tests (run **only after human confirmation**).
3. Live check on a locally built Mcp server: the exact repro returns
   `Unknown column 'Path'. Available columns: …`.

## Planned commits

1. `feat(sql): schema-validated unknown-column diagnostics for sql_query (SqlQueryValidator)` —
   validator + fail-fast hook; WHERE/ORDER BY/GROUP BY/HAVING/projection (single real table);
   replaces reflection checks; tests incl. exact repro.
2. `feat(sql): extend unknown-column validation to multi-table, JOIN ON, CTE, derived tables` —
   tests.
3. `fix(sql): position-less parse errors get token-search caret + stray-semicolon hint` — both
   hosts' `ParseErrorFormatter`; tests.
4. `docs: document schema-first sql_query column validation in AGENTS.md`

## Blast radius

- `src/CodeMemory.Mcp/SqlQuery/SqlQueryValidator.cs` (new) + `SqlQueryService.cs` (hook,
  projection check replacement) — private internals; downstream: `SqlQueryTool.SqlQueryAsync`
  (Mcp tool surface), Join/CTE/subquery paths, `SqlQueryServiceTests` /
  `SqlQueryServiceJoinTests` / `SqlQueryServiceBenchmarkTests`, `Mcp/AspNetSqlQueryToolTests`.
- `SqlExpressionBuilder.resolveProperty` — message only (safety net).
- `ParseErrorFormatter.cs` in **both** hosts — shared-format change; `AspNetSqlQueryToolTests`
  line ~201 asserts `Parse error at line …` (kept), plus new formatter tests.
- `AGENTS.md` — docs only.
- No public API / MCP schema changes. No ADR needed (bug fix consistent with existing intent).
- Deliberate behavior changes: (a) previously-*silent* wrong results in ORDER BY / GROUP BY /
  HAVING / multi-table WHERE/ON / multi-table projection now become explicit errors;
  (b) multi-table unqualified identifiers require qualification (see Decision 2).

## GitHub issues log

- [x] #122 — MCP SQL engine: unknown column reported as cryptic parse error (misleading
  position). **This plan fixes it.**
- [ ] Related-but-not-fixed: #124 — schema introspection (PRAGMA/DESCRIBE) documentation — out of
  scope here, revisit separately.
- [ ] (per skill rules: create immediately and record here if any deferred work surfaces during
  execution, e.g. deep `InSubquery`/derived-table recursion if it balloons)