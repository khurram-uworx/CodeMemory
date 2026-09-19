# Plan: Fix #127 — shared non-thread-safe `SqlQueryParser` corrupts concurrent `sql_query` calls

## Problem

Parallel `sql_query` MCP calls corrupt each other's results. Live reproduction against the latest
build (`0.6.0+039160b.`, i.e. after all #120 fixes):

- 4 concurrent copies of the *same* valid query → 1 of 4 fails with a spurious parse error at a
  token position that can never signal an error (`COUNT(*)` → `Column 15` pointing at `)`).
- 4 concurrent *different* valid queries → parse errors mention tokens that are not in the
  submitted query (comma from `SELECT Name, FilePath` leak into `SELECT RowId …`), and queries fail
  at parse instead of reaching the (fixed in #128) projection validation.
- Sequential execution of the same queries: always correct.

Root cause: `SqlQueryParser` wraps sqlparsercs's `Parser`, which keeps mutable instance state
(`_tokens`/`_index`) reused across `ParseSql` calls and is **not thread-safe**. Both hosts hold a
single shared `readonly SqlQueryParser parser = new();` instance:

- Mcp host: `src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs` line 1093 — used at line 2093
  (`parser.Parse(sql.AsSpan(), Dialect)`). `SqlQueryService` is registered `AddSingleton` in
  `src/CodeMemory.Mcp/Program.cs` line 113, so every concurrent `sql_query` call shares the parser.
- AspNet host: `src/CodeMemory.AspNet/Tools/AspNetSqlQueryTool.cs` line 228 — used at line 294 in
  `validateQuery`. Tool instance lifetime is SDK-managed (types are discovered via
  `WithToolsFromAssembly`, not registered with our own lifetime), so exposure depends on SDK
  behavior; treat as shared anyway.

Scope check of the rest of the query path (all thread-safe, no other changes needed):

- `SqlExpressionBuilder` — all-static methods, no instance state.
- `CollectionRegistry` — `entries` dictionary written only in the constructor; read-only after.
- `TableSchemaProvider` — stateless; `KnownJoinKeys` static readonly.
- Static caches in `SqlQueryService` — `ConcurrentDictionary`.

The parser field is the **only** shared mutable state in the query path.

## Proposed changes

**Fix (Option A — per-call parser).** Replace the shared instance field with a locally-created
parser at each parse site:

- `SqlQueryService.cs`: remove `readonly SqlQueryParser parser = new();` (line 1093); at line 2093
  create `var parser = new SqlQueryParser();` before `parser.Parse(sql.AsSpan(), Dialect)`.
- `AspNetSqlQueryTool.cs`: remove `readonly SqlQueryParser parser = new();` (line 228); in
  `validateQuery` (line 294) create `var parser = new SqlQueryParser();` before
  `parser.Parse(query.AsSpan(), Dialect)`.

Rationale vs Option B (`lock` around the shared parser): Option A has zero contention, no
serialization of unrelated queries, no latent shared-state hazard, and trivial allocation cost (the
tokenizer is created per call anyway). Option B leaves shared mutable state in place — strictly
worse. DI registration stays `AddSingleton` (service is now stateless for the query path).

**Regression tests.** One shared service/tool instance per test (models the production singleton),
parallel calls via `Task.WhenAll`:

1. `SqlQueryServiceTests` (uses existing `createServices()` + `seedSymbolsAsync`):
   - `SqlQuery_ConcurrentIdenticalQueries_AllSucceed` — 8 parallel
     `SELECT COUNT(*) AS Total FROM SymbolRecord` × ~10 rounds: every result `success:true` with
     `Total == 5` (5 seeded symbols).
   - `SqlQuery_ConcurrentDistinctQueries_EachReturnsOwnRow` — 8 parallel queries each filtering a
     distinct sentinel `Name`: each result contains exactly its own row, nothing else.
2. `AspNetSqlQueryToolTests`:
   - `SqlQueryAsync_ConcurrentCalls_AllSucceed` — parallel `SqlQueryAsync` on a single
     `CreateToolWithData()` instance.

Concurrency (8-way × multiple rounds) makes the pre-fix suite fail near-certainly; post-fix is a
deterministic pass.

**Docs.** AGENTS.md "Common Pitfalls": add note that `SqlQueryParser` (sqlparsercs `Parser`) is not
thread-safe — never share across concurrent calls; create per call (see #127).

## Verification

1. `dotnet build` (solution) — fixes compile in both hosts.
2. `dotnet test` — full suite (currently 562 tests) + new concurrency tests; run only after human
   confirmation.
3. Live MCP check (after human pushes/merges or on a locally built server): 4+ parallel `sql_query`
   calls → all succeed correctly (currently ~1-in-4 corrupt).

## Planned commits

1. `fix(mcp): per-call SqlQueryParser in SqlQueryService — no shared parser state` (Mcp host fix)
2. `fix(aspnet): per-call SqlQueryParser in AspNetSqlQueryTool.validateQuery` (AspNet host fix)
3. `test(sql): concurrency regression tests for parallel sql_query (SqlQueryService + AspNet tool)` (tests)
4. `docs: note SqlQueryParser is not thread-safe in AGENTS.md Common Pitfalls` (docs)

## Blast radius

- `src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs` — the `parser` field is private; only internal
  callers at line 2093. Downstream: `SqlQueryTool.SqlQueryAsync` (mc tool surface) and the Join /
  subquery paths in `SqlQueryServiceJoinTests` / `SqlQueryServiceBenchmarkTests` / related suites.
  Tests link this source via `tests/CodeMemory.Tests/CodeMemory.Tests.csproj` → fix applies to test
  compile automatically (no csproj change).
- `src/CodeMemory.AspNet/Tools/AspNetSqlQueryTool.cs` — touched only where the parser is created;
  covered by `AspNetSqlQueryToolTests`.
- `AGENTS.md` — docs-only.
- No public API / MCP schema changes. No ADR needed (bug fix consistent with existing intent).

## GitHub issues log

- [x] #127 — concurrency: shared non-thread-safe `SqlQueryParser` corrupts parallel `sql_query`
  (root cause + live repro already documented as a comment on the issue). **This plan fixes it.**
- [ ] (none new expected; per skill rules, create immediately and record here if any deferred work
  surfaces during execution)