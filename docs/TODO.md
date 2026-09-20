# Plan: Fix SQL JOIN hang (issue #126)

Branch: `khurram/126` · Milestone: 0.7 · Issue: #126

## Problem

Any `sql_query` that JOINs `RelationshipRecord` with `SymbolRecord` on
`SourceSymbolId`/`TargetSymbolId = Id` never completes — the MCP call runs indefinitely
(reporter observed 30–60s+; reproduced live at **13,555 ms on a ~5× smaller index**).
This blocks the core advertised workflow of navigating code via relationships (call graphs,
blast radius).

Root cause (verified against `v0.6.0` == current running MCP server; **unchanged on main**
— `git diff v0.6.0..HEAD -- src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs` touches only
parser/validator wiring):

1. **O(n·m) nested-loop equi-join** — `innerJoin`/`leftJoin`
   (`src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs:1580–1621`) iterate every pair and
   allocate a merged `Dictionary` per pair (`mergePair`). The reported two-join query is
   `2 × (relationships × symbols)` pair evaluations.
2. **Fully materialized, unfiltered fetches** — `evaluateGroupAsync`
   (`SqlQueryService.cs:1772, 1824`) loads every join source via
   `queryFilteredAsync(store, entry, null, int.MaxValue, ct)` — no WHERE pushdown, no fetch limit.
3. **No LIMIT pushdown** — `LIMIT 5` is applied only after the full join closure is built
   (`ExecuteAsync`, `SqlQueryService.cs:2316–2317`); `executeJoinQueryAsync` ignores limits.

Secondary (already fixed on main by #122, `6ab61d8`, verified by
`JoinOn_UnknownColumn_ReturnsClearError`): v0.6.0 does not validate JOIN `ON` columns, so a
wrong column (`ON r.SourceId = s.Id`) silently returned 0 rows — at scale that *looks* like a
hang too. No work needed on main for this; the hang fix is the only remaining defect.

## Proposed changes

### Task 1 — Hash equi-join optimization (primary fix)

`src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs`

When the ON condition is an equi-join (AND-tree of `Eq(CompoundIdentifier, CompoundIdentifier)`
where exactly one operand's prefix is the right-side prefix), replace the nested loop with a
hash join:

- Build a composite-key index over the right rows: `Dictionary<string, List<row>>` keyed by
  the right-side prefix+column value(s).
- Probe with each left row's key value; emit only merged pairs on hits.
- Any residual conjuncts (non-Eq predicates in ON) are re-evaluated per candidate for
  correctness (standard hash-join residual filter).
- `null` key values probe nothing (matches SQL equality semantics).
- Fall back to the existing `mergeWithJoinType` nested loop for cross joins, non-equi
  predicates, and RIGHT/FULL OUTER (correctness first).

Key extraction must be **prefix-aware and direction-agnostic**: for each `Eq(x, y)` conjunct,
an operand whose prefix equals the right-side prefix is the right key; the other operand is the
left key — so the second join (`ON r.TargetSymbolId = t.Id`, left prefix `r`, not the most
recent `s`) hashes correctly. If both operands land on the same side, fall back to nested loop.

Sketch:

```csharp
static (List<(string leftKey, string rightKey)> keys, AstExpr? residual)
    TryExtractEquiJoin(AstExpr? onCondition, string rightPrefix)
// keys are prefixed row keys ("r.SourceSymbolId" / "s.Id"); composite keys joined with '\0';
// residual = rest of the AND-tree, or null.

static List<Dictionary<string, object?>> HashEquiJoin(
    List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right,
    List<(string, string)> keys, AstExpr? residual, int? budget)
```

### Task 2 — LIMIT pushdown (bonus bound for fallback paths)

Thread the effective `top` (limit or maxResults) into `executeJoinQueryAsync` /
`evaluateGroupAsync` and allow early exit **only when semantics permit**
(no ORDER BY, no GROUP BY/aggregates, no DISTINCT). Budget is applied inside the merge for
INNER/LEFT/CROSS; any emitted subset of a no-ORDER-BY result is valid SQL. This bounds cross
joins and nested-loop fallbacks, and makes the hash path short-circuit at `top` rows.

### Task 3 — Regression test at issue scale

`tests/CodeMemory.Tests/Services/Query/SqlQueryServiceJoinTests.cs`

Seed ~13k relationships + ~3k symbols (mirroring the issue), run the reported query with
`LIMIT 5`, assert correct rows and completion well under a tight budget (e.g. `< 5 s`).
Keep all existing join tests green (they pin correctness of INNER/LEFT/RIGHT/FULL/USING/
nested/cross joins).

### Task 4 — Documentation

`AGENTS.md` (§SQL Parser Development Reference): note the equi-join fast path and the
expectation that relationship traversal queries return in milliseconds at index scale.

## Verification steps

1. `dotnet build CodeMemory.slnx` (or test project) after each task.
2. Targeted test run of `SqlQueryServiceJoinTests` (+ scale test) — **ask human before
   running `dotnet test`** (long-running verification requires confirmation per skill).
3. Live MCP repro after the fix: re-run the issue query via sql_query and confirm
   `executionTimeMs` drops from ~13,500 ms to single-digit/low-hundreds ms.
4. Full `dotnet test` run (human-confirmed) before removing the plan.

## Planned commits

1. `docs: plan issue 126 SQL JOIN hang in TODO.md`
2. `fix(sql): hash equi-join for SQL JOIN queries` (Task 1 + Task 2)
3. `test(sql): regression coverage for issue #126 JOIN at index scale` (Task 3)
4. `docs: document equi-join SQL fast path` (Task 4)
5. G2 review fixes (if any) → `docs: remove TODO.md — plan executed`

## Blast radius

- **Core change file**: `src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs` — join execution
  used by both hosts (`SqlQueryTool` STDIO MCP, `AspNetSqlQueryTool`). Multi-table path only;
  single-table/CTE/UNION/vector-search paths untouched.
- **Behavior**: identical results for INNER/LEFT equi-joins (hash join must preserve merge
  order for LEFT; dedupe/multi-match semantics via `List` buckets); no API surface change
  (`SqlQueryService.ExecuteAsync` signature unchanged).
- **Fallbacks retain current (slow but correct) behavior** for non-equi/outer joins — bounded
  by Task 2's budget where semantics allow, otherwise unchanged.
- **Tests**: `SqlQueryServiceJoinTests` (14 join tests) + `SqlQueryServiceTests` pin the
  contract; `SqlQueryServiceBenchmarkTests.StressTest_*` guards broad latency.
- No ADR impact (this is implementation detail inside an existing engine, no new
  framework/abstraction).

## Grounding (Resistance G1)

Internal notes — hash-join pattern is standard relational-engine technique (build phase on
smaller/probed side, probe with driving side, residual predicate re-evaluation). Grounded
against microsoft-learn ("Joins (SQL Server)": build phase + probe phase + residual predicate
for correctness) — confirms the plan. Grounding surfaced one refinement, adopted during
implementation: the `LIMIT` early-exit budget is additionally gated on the query having **no
`WHERE` clause** (a join prefix could otherwise under-fill a WHERE-filtered result; a subset
is only a valid no-ORDER-BY result without a filter). This narrows the optimization and
changes no semantics.

## Verification & probe lifecycle

Timed verification lived directly in the regression suite — no standalone temp harness was
needed, so nothing to promote/clean up:

- `JoinScale_ReportedQueryWithLimit5_ReturnsFiveRowsQuickly` — 196 ms (pre-fix nested loop:
  ~13.5 s live on 2.5k/4.3k index; tens of seconds at the test's 10k×3k scale)
- `JoinScale_FullClosureCount_CompletesUnderBudget` — 326 ms (full 10k-row hash join closure)
- `JoinOn_WrongColumnName_Issue126_ReturnsErrorNotHang` — 122 ms (schema-first validation)

These double as the permanent regression gate for #126.

## GitHub issues log

- [ ] #126 — SQL JOIN between RelationshipRecord and SymbolRecord hangs indefinitely
      (working on: khurram/126 plan/implementation)
- [x] #137 — RIGHT/FULL OUTER and non-equi SQL JOINs still run O(n·m) nested loops
      (created while working on: khurram/126 — residual hazard deliberately left in scope;
      nested-loop fallback for those join shapes remains a hang risk at index scale)

---

Reminder: as each task executes, if deferred work or a concern surfaces, create a GitHub
issue immediately (`gh issue create --repo khurram-uworx/CodeMemory`) and record its number
here — don't rely on memory or wait until the end.