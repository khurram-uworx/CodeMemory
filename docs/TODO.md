# TODO — Issue #120: silent empty refs, SQL parser gaps, doc-heavy semantic search

Branch: `khurram/120` (base `main`)

## Problem

Issue #120 (https://github.com/khurram-uworx/CodeMemory/issues/120) reports five MCP-tool pain
points from real agent source-navigation work. Verified status in the current source:

1. **`find_related_code` silently returns `[]` for method symbol paths.** Two distinct causes:
   - **Cause A — symbol not found:** `StorageService.GetSymbolByFullNameAsync` does exact
     `FullName == x` then `Name == x`. Method `FullName`s include parameter lists
     (`...getLikeMethod(string pattern)`), so the bare path `...getLikeMethod` miss `es` and the
     tool returns `[]` with no diagnostic. Verified on the live MCP server.
   - **Cause B — relation-type filter masks data:** with the exact signature and
     `relationType: "references"`, the tool returns `[]` even though the method has inbound
     relationships — they are typed `Calls`, not `References`, and the literal filter drops them.
     Verified via `sql_query` against `RelationshipRecord`.
2. **SQL parser gaps** (`LIKE` with `/`, `COUNT(*)`) — already fixed in current source/build
   (verified: `LIKE '%Optimizer/SGD.cs%'` parses; `SELECT COUNT(*)` returns `2214`). Remaining
   work is **regression coverage** so they cannot silently regress, plus one **new silently-wrong
   result** found during planning: `SELECT RowId FROM SymbolRecord` returns `[{},{}]` rows with a
   phantom column instead of erroring (SELECT projection identifiers are never validated, unlike
   WHERE which throws via `SqlExpressionBuilder.resolveProperty`).
3. **Parser error messages are internal dumps.** `Parse error: Expected Expected an expression,
   found: Identifier { Ident = FROM }` — duplicated "Expected", Rust-style debug dumps, no
   position relative to the submitted query. Root cause is upstream in `SqlParser-cs`
   (`Parser.cs:6886` wraps `Expected("Expected an expression, …")` while `Expected()` prefixes
   another "Expected"; `Found()` appends a debug-formatted token). `ParserException` /
   `TokenizeException` both expose 1-based `Line`/`Column` — usable for a caret diagnostic.
4. **`semantic_search` returns doc-heavy results for code queries.** `ChunkRecord.Language` is
   `"Text"` for `.md`/`.txt` (`LanguageDetector.extensionMap`), code languages otherwise — so a
   deterministic `codeOnly` filter is feasible without score hacks.

## Proposed changes

TDD: for each slice, write the failing test(s) reproducing the issue first, then implement, then
validate (targeted), then run regression (full suite). Tests follow repo conventions
(NUnit `[Test]` only, no `[TestCase]`, `Method_Scenario_ExpectedBehavior`, `BaseToolTests` /
`BaseServicesTests`, `MockServices` factory).

### 1. Symbol resolution + `find_related_code` diagnostics

- **Storage layer (root cause A, helps all tools):** extend the fallback chain in
  `GetSymbolByFullNameAsync` (`StorageService`, `HybridStorageService`, `StorageServiceRouter`
  passthrough): exact `FullName` → `FullName.StartsWith(fullName + "(")` (signature-insensitive)
  → `Name == fullName`. First hit wins; ambiguity (overloads) resolves to first — documented.
- **Suggestions surface:** add `SuggestSymbolsAsync(string query, int top)` to `IStorageService`
  (prefix match on `FullName` and on `Name` last segment, distinct, top N) for the not-found
  diagnostic. Implement in `StorageService`, `HybridStorageService`, `StorageServiceRouter`.
  NSubstitute mocks in tests need no change.
- **Tool layer (diagnostics):** `FindRelatedCodeTool.FindRelatedCodeAsync` returns a typed record
  `FindRelatedCodeResult(Results, MatchedSymbol, Message, Suggestions)` instead of a bare
  `IReadOnlyList<DependencyNode>`. When results are empty:
  - symbol resolved → message "no '{relationType}' relationships found for '{path}'" plus the
    available relationship types for that symbol (distinct types across
    `GetRelationshipsByTargetAsync` / `GetRelationshipsBySourceAsync`);
  - symbol not found → message "symbol not found" plus `Suggestions`.
  This is a **breaking MCP schema change** (array → object) — confirmed with the human at G1.
  `TraceDependencyTool` / `ImpactAnalysisTool` share `FindRelatedAsync` and benefit from the
  storage-layer fix; their return types are left unchanged (out of scope, note in issues log if
  a follow-up is warranted).

### 2. SQL: validate SELECT projection columns (silent `{}` rows)

- Before projecting, validate every plain-identifier selected column (and aggregate args)
  against the record type when the source is a single real table (`entry.RecordType` via
  `schemaProvider.GetColumns` or record properties). Missing column →
  `fail("Column 'RowId' not found on 'SymbolRecord'. Available: Id, Name, ...")` — same shape as
  `SqlExpressionBuilder.resolveProperty` already produces for WHERE.
- CTE / multi-table projections stay result-driven (unchanged).

### 3. SQL: human-readable parse errors with position (both hosts)

- In `SqlQueryService.ExecuteAsync` (parse catch) and `AspNetSqlQueryTool`, catch
  `ParserException` / `TokenizeException` specifically and format:
  `Parse error at line {L}, column {C}:` followed by the offending SQL line and a caret, then the
  sanitized message.
- Sanitizer (small static helper, unit-tested): collapse `Expected Expected` → `Expected`;
  convert `Identifier { Ident = X }` → `identifier 'X'`; strip trailing re-appended
  `, found ...` duplication where present. Workaround for upstream `SqlParser-cs` behavior — no
  library change.

### 4. `semantic_search` `codeOnly` filter

- Add `codeOnly` (default `false`, non-breaking) parameter to
  `SemanticSearchTool.SemanticSearchAsync`; when true, drop results whose
  `r.Chunk.Language == "Text"` (`.md`/`.txt` docs). Document in the tool description: "exclude
  documentation (.md/.txt) chunks — code only".

### 5. Regression coverage (issues 2/3 lock-in)

- `SqlQueryServiceTests`: LIKE pattern containing `/` and other specials; `COUNT(*)` total;
  `SUM`/`AVG` active; `SELECT DISTINCT col` (guards the transient "Index was out of range" seen
  during planning); `SELECT RowId …` → clear error (from slice 2).

### 6. AGENTS.md — development reference

- Add a note (Common Pitfalls / Development references): SQL parser is NuGet `sqlparsercs`
  (C# port of sqlparser-rs); upstream source cloned at `E:\github\SqlParser-cs`; message quirks
  (doubled "Expected") originate upstream; `ParserException`/`TokenizeException` expose
  `Line`/`Column`.

## Files likely involved

- `src/CodeMemory/Storage/IStorageService.cs`, `StorageService.cs`
- `src/CodeMemory.AspNet/Storage/HybridStorageService.cs`, `StorageServiceRouter.cs`
- `src/CodeMemory/Mcp/FindRelatedCodeTool.cs` (+ result record)
- `src/CodeMemory.Mcp/SqlQuery/SqlQueryService.cs`, `SqlExpressionBuilder.cs`
- `src/CodeMemory.AspNet/Tools/AspNetSqlQueryTool.cs`
- `src/CodeMemory/Mcp/SemanticSearchTool.cs`
- `AGENTS.md`, `docs/TODO.md`
- Tests: `FindRelatedCodeToolTests.cs`, `StorageServiceTests.cs`, `SqlQueryServiceTests.cs`,
  `SemanticSearchToolTests.cs`, (AspNet SQL tool tests if present)

## Verification steps

1. Targeted: new tests for each slice (run after implementation, human-approved).
2. Full suite `dotnet test` (human-approved, per skill).
3. Manual-ish spot checks via code-memory MCP `sql_query` where a live server is available.

## Planned commits (one logical change each)

1. `docs: plan issue #120 fixes in TODO.md`
2. `docs: note SqlParser-cs source location in AGENTS.md`
3. `fix(sql): validate SELECT projection columns with clear errors` (+ tests)
4. `fix(sql): human-readable parse errors with line/column and caret` (+ tests, Mcp + AspNet)
5. `test(sql): regression coverage for LIKE '/', COUNT(*), DISTINCT`
6. `fix(storage): signature-insensitive symbol resolution + suggestions` (+ tests, all storage impls)
7. `fix(mcp): find_related_code typed result with diagnostics and suggestions` (+ tests)
8. `feat(mcp): semantic_search codeOnly filter` (+ tests)
9. `docs: remove TODO.md — plan executed` (after G2)

## Blast radius

- `GetSymbolByFullNameAsync` behavior change affects every tool that resolves symbols:
  `find_related_code`, `trace_dependency`, `get_edit_context`, `get_symbol_history`,
  `impact_analysis`. Direction of change: previously-empty method lookups now resolve. Risk:
  overload ambiguity resolves to first match (documented); behavior is otherwise additive.
- `IStorageService` gains one method → `StorageService`, `HybridStorageService`,
  `StorageServiceRouter`; NSubstitute mocks unaffected.
- `find_related_code` MCP schema change (array → object) is breaking for agents consuming the
  current schema (schema docstring updated; tests updated). Confirmed with human at G1.
- `semantic_search`, SQL error messages: additive / message-shape only. No vector-store or
  indexing changes. SqlParser-cs library untouched.

## GitHub issues log

- (none yet — opened during execution via `gh issue create --repo khurram-uworx/CodeMemory` if
  deferred work surfaces; recorded here immediately)