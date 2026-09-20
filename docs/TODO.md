# Plan: Issue #137 — RIGHT/FULL OUTER and non-equi SQL JOINs still run O(n·m) nested loops

## Problem

The hash equi-join from #126 covers `INNER`/`LEFT` joins with an equi-join `ON`
(equality between distinct-prefixed columns). Every other join shape still evaluates a full
nested loop with a per-pair `Dictionary` allocation:

- `RIGHT OUTER` / `FULL OUTER` joins (any `ON`)
- `ON` predicates that are not an AND-tree of prefix-prefixed equality conjuncts (`<`, `>`,
  `LIKE`, `OR`, …)
- Comma-joins / `CROSS JOIN` where a `WHERE` filter is present (cross product unfiltered
  upstream of the join merge)

Repro on the current tree (v0.6.0, index 4,396 relationships × 2,455 symbols ≈ 10.8M pairs):

| Shape | Time | Result |
|---|---|---|
| `INNER JOIN … ON r.SourceSymbolId = s.Id` (equi, hash path) | 0.8s | 4,396 ✓ |
| `RIGHT JOIN … ON r.SourceSymbolId = s.Id` | 27.7s | 5,404 |
| `FULL OUTER JOIN … ON r.SourceSymbolId = s.Id` | 72.4s | **9,800 — wrong, see below** |
| `INNER JOIN … ON r.SourceSymbolId <> s.Id` (non-equi) | 74.6s | 10.8M-row closure |

Nested-loop cost measured ≈ 2.6µs/pair, so an unbounded closure ≥ ~1M pairs is already a hang.

**Bonus defect found during baseline:** `fullOuterJoin` dedups on
`string.Join('\0', row.Values…)`, and the two `leftJoin` passes (`left,right` and `right,left`)
merge dicts in opposite column order — so identical logical matched rows produce different dedup
keys and are **double-counted** (engine returns 9,800 where the correct answer is 5,404).

## Proposed changes

All in `src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs` (+ one new exception type).

### Change 1 — hash fast path for RIGHT and FULL OUTER

Generalize `hashEquiJoin(left, right, keys, residual, bool isLeft, int? budget)` (line ~1757)
to take the full `JoinType`. Keep `MergeLeft` semantics = emit merged row then `mergePair(l, r)`
(left-major). Behavior per type:

- `Inner` / `LeftOuter` — unchanged (index right rows, probe each left row, residual per
  candidate, preserve unmatched left with null right columns when `isLeft`).
- `RightOuter` — reverse probe: build the index over the running **left** set keyed by the left
  key, probe with each right row keyed by the right key, merge as `mergePair(l, r)`. Unmatched
  right rows emit null-left + right. Right-major emission keeps a `LIMIT` budget prefix-valid
  (every right row is preserved, so any prefix of the right-major stream is a valid prefix of the
  RIGHT result).
- `FullOuter` — LEFT-style probe (index the right side), track which right row instances matched
  (reference-equality `HashSet`); after the pass, emit unmatched right rows (null-left) at the
  end. **No budget early-exit** for FULL (a prefix cannot represent the closure). The single
  canonical merge order fixes the double-count defect above — no dedup pass needed.

Helpers: generalize null-fill column extraction to both sides
(`leftNullColumns`/`rightNullColumns` — currently only the right side exists).

Dispatch gate in `evaluateGroupAsync` (line ~2030): widen
`joinType is JoinType.Inner or JoinType.LeftOuter` to include `RightOuter` and `FullOuter`;
pass `rowBudget` for Inner/Left/Right and `null` for Full. `tryExtractEquiJoin` is
direction-symmetric — no change.

### Change 2 — fail-fast guard for non-extractable nested loops

Const `MaxNestedLoopPairs = 1_000_000` (~2.6s of nested-loop work at measured cost; keeps all
small non-equi joins working).

Enforce whenever a nested loop would run with `budget is null`: non-equi `innerJoin` /
`leftJoin` / `fullOuterJoin` / the `RightOuter` leftJoin fallback / `crossJoin` (covers
comma-join-with-`WHERE` and 3+-table comma joins, which the issue calls "unprotected WHERE-filtered
cross products"). Throw `SqlQueryJoinTooLargeException` with a repo-style diagnostic (shape, pair
count, guidance: rewrite as equi `ON a.x = b.y`, add `LIMIT`, or filter inputs).

New `sealed class SqlQueryJoinTooLargeException : InvalidOperationException` in
`src/CodeMemory.Mcp/SqlQuery/` (one class per file). Add
`catch (SqlQueryJoinTooLargeException ex)` **before** the generic `catch (Exception)` in
`ExecuteAsync` (line ~2553) so it returns a clean `fail(ex.Message, sw)` instead of the
"Execution error at stage … for SQL …" wrapper.

Budgeted nested loops keep today's behavior (matches the issue's scope — the hang is the unbounded
closure).

### Change 3 — regression tests

`tests/CodeMemory.Tests/Services/Query/SqlQueryServiceJoinTests.cs`, reusing
`seedScaleJoinDataAsync(symbolCount, relationshipCount)`:

- `RIGHT JOIN`/`FULL OUTER JOIN` equi counts on the issue shape complete <5s with the **correct**
  totals (FULL with left-orphan and right-orphan rows — asserts the double-count fix).
- Non-equi `ON` at scale fails fast (<5s, `Success == false`, diagnostic mentions the equi-join
  guidance).
- Existing small-seed RIGHT/FULL tests (`CrossJoin_RightJoinSyntax_ReturnsRightOuterRows`,
  `CrossJoin_FullOuterJoinSyntax_ReturnsAllRows`) must keep passing through the new hash path.
- Small non-equi joins (`LIKE`-based cross joins) still produce rows (guard does not fire).

### Change 4 — docs

Update the AGENTS.md §SQL hash-join paragraph: RIGHT/FULL OUTER now hash; non-equi/unbounded
nested loops fail fast above the pair ceiling (with the #126 sentence corrected to note FULL
dedup no longer double-counts).

## Verification steps

1. `dotnet build` (ask human to restart the harness first if the FileLocked build fails while the
   MCP server runs).
2. `dotnet test` targeted:
   `dotnet test tests/CodeMemory.Tests --filter "FullyQualifiedName~SqlQueryServiceJoinTests"`.
3. Full `dotnet test` suite for regressions.
4. After a harness restart (rebuild + reindex), re-run the four baseline queries above via the
   code-memory MCP `sql_query` tool: equi 0.8s stays, RIGHT and FULL drop to <5s, FULL count =
   5,404, non-equi fails fast with the diagnostic. Also issue the exact #137 repro.

## Planned commits

1. `docs: plan issue 137 SQL join perf in TODO.md` — ✅ landed
2. `fix(sql): hash join for RIGHT/FULL OUTER joins` (Change 1) — ✅ `9c2c013`
3. `fix(sql): fail-fast diagnostic for unbounded nested-loop joins` (Change 2) — ✅ `a4b5335`
4. `test(sql): regression coverage for issue #137` (Change 3) — ✅ `99284aa`
5. `docs: document RIGHT/FULL hash join and nested-loop guard` (Change 4) — ✅ `ce3d974`
6. (additional, found during test verification) `fix(sql): resolve qualified ORDER BY columns unambiguously in joins` — ✅ `57ce889`
7. (additional, found during live verification) `fix(sql): flow LIMIT early-exit budget into non-equi RIGHT OUTER` — ✅ `8c3fcda`

## Execution log (vs plan)

- All four planned changes landed as planned, plus one additive fix discovered when the newly
  widened hash dispatch ran the pre-existing `CrossJoin_RightJoinSyntax_ReturnsRightOuterRows`
  test: `ORDER BY r.Id` was stripped to `Id` and resolved by first `.Id`-suffixed row key, so the
  effective key depended on merged-dict order (old RIGHT path = right-side keys first; new hash
  RIGHT = left-side keys first) and the sort silently flipped. Fixed in `applyOrderBy` by
  preferring the fully-qualified compound name when a stripped sort key is ambiguous across join
  sides; UNION ORDER BY (stripped-name semantics) untouched. Pinned by new
  `JoinOrderBy_QualifiedAmbiguousColumn_SortsByDeclaredSide` (fails pre-fix).
- Test expectation corrected during verification: the small non-equi RIGHT join on the seed
  (3 relationships) is 15 rows, not 21 — the initial arithmetic wrongly included an orphan
  relationship that is only upserted by the dedicated orphan tests.
- Live MCP verification (after human restarted the harness on the `dotnet run` variant — the
  server now embeds `167eed5`, this branch): equi INNER 798ms/4,429; RIGHT equi 239ms/5,442
  (baseline 27.7s); FULL equi 190ms/5,442 = RIGHT (no orphan rels; baseline 72.4s and a
  double-counted 9,800); non-equi FULL `<>` fails fast in 28ms with the diagnostic
  (10,930,772 pairs > 1M; baseline 74.6s hang). That check also surfaced one more gap: a
  LIMIT-bounded non-equi RIGHT join ran the full closure anyway (135s for LIMIT 5) because
  `mergeWithJoinType` dropped the early-exit budget on the RightOuter arm — fixed in `8c3fcda`,
  pinned by `JoinNonEqui_RightOuterWithLimit_EarlyExitsUnderBudget`. Live re-check of the LIMIT
  case pending the server rebuild (harness restarted once more).
- No extra probe project created — runtime smoke checks used the repo's own NUnit suite
  (`tests/CodeMemory.Probes` untouched). The temporary `Tmp137Debug.cs` diagnostic test was
  removed after use (see probe lifecycle guidance).

## Blast radius (post-execution)

Unchanged from plan: `SqlQueryService.cs` + new `SqlQueryJoinTooLargeException.cs` (Mcp), the test
project's linked-source list (`CodeMemory.Tests.csproj`), AGENTS.md. The additional `applyOrderBy`
ambiguity fix touches the shared ORDER BY path used by every query with ORDER BY — covered by the
full suite (656 green).

## Blast radius

- `src/CodeMemory.Mcp/SqlQuery/`: `SqlQueryService.cs` join/merge paths, one new exception file.
- Behavior surface: the `sql_query` MCP tool (and anything routing through
  `SqlQueryService.ExecuteAsync` — `AspNetSqlQueryTool` in `CodeMemory.AspNet`). Right/full equi
  joins change from slow-but-correct to fast-and-correct (FULL count bug fixed); huge non-equi
  unbounded joins change from hang → clean error.
- No library (`CodeMemory`) changes; no storage/schema changes; no new dependencies.
- Tests covering the touched surface: `SqlQueryServiceJoinTests` (all), plus any caller of
  `SqlQueryService` (Mcp + AspNet SQL tools are covered by their own suites — full `dotnet test`
  catches drift).
- Convention risk: fail-fast error for large non-equi joins is a behavior change for the tool
  (previously "eventually returns"); the issue explicitly endorses it ("or, at minimum, they
  should fail fast with a clear diagnostic instead of appearing to hang").

## Reminder

As each task executes: if you find deferred work or a concern (known limitations, follow-ups,
refactors) outside this plan, create a tracked GitHub issue immediately
(`gh issue create --repo khurram-uworx/CodeMemory`) and record its number below — never hold it in
memory.

## GitHub issues log

- [ ] #137 — RIGHT/FULL OUTER and non-equi SQL JOINs still run O(n·m) nested loops (this plan)