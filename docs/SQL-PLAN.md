# SQL Surface for InMemoryVectorStore — Task Breakdown

## Purpose

Break the SQL query surface feature into concrete, assignable tasks for coding agents. Each task is small enough for one agent to own end-to-end, with clear acceptance criteria and file lists.

## How To Use

- Execute tasks in order (each depends on the previous).
- After Task 1 physical file changes, the MCP index should be rebuilt, consider: `dotnet build` and `dotnet test` after each phase.
- Task 1, 2, 3 are pure implementation; Task 4 depends on them and integrates everything.
- Task 5 is polish/docs that can run after integration.

## Suggested Execution Order

1. Task 1: Expose VectorStore from StorageService
2. Task 2: Build SqlExpressionBuilder (SQL AST → LINQ expression)
3. Task 3: Build CollectionResolver + SqlQueryService
4. Task 4: Replace SqlQueryTool + wire up DI
5. Task 5: Self-test against the CodeMemory repo index

## Coordination Notes

- Task 1 is a trivial one-liner but is a dependency for Tasks 3 and 4.
- Tasks 2 and 3 can share the `SqlQuery/` directory but Task 3 depends on the builder from Task 2.
- Task 4 replaces the existing `SqlQueryTool.cs` completely — no merge concern since it's a single file.
- All new files go in `src/CodeMemory.AspNet/SqlQuery/`.
- No existing functionality is modified (GraphQueryTool stays untouched).

---

## Task 1: Expose VectorStore from StorageService

### Priority

High

### Goal

Add a public property on `StorageService` and `IStorageService` so the SQL query service can access the raw `VectorStore` to create typed collections.

### Why this exists

Today `VectorStore` is a private readonly field in `StorageService`. `SqlQueryService` needs `vectorStore.GetCollection<string, TRecord>(name)` to query collections. No existing API exposes the store.

### Scope

- Add `VectorStore? Store { get; }` to `IStorageService`
- Implement it in `StorageService` (returns the `vectorStore` field)
- Implement it in `LiteGraphStorageService` (returns `null`)
- Forward it in `StorageServiceRouter.GetStorage()` delegate

### Constraints

- Must not break existing callers (returns `null` for non-VectorStore backends)
- Must preserve the existing `IStorageService` contract

### Acceptance criteria

- `IStorageService` has a `VectorStore? Store { get; }` property
- `StorageService.Store` returns the concrete `InMemoryVectorStore` or `SqliteVectorStore`
- `LiteGraphStorageService.Store` returns `null`
- All existing tests still pass

### Files likely involved

- `src/CodeMemory/Storage/IStorageService.cs`
- `src/CodeMemory.Storage/StorageService.cs`
- `src/CodeMemory.AspNet/LiteGraph/LiteGraphStorageService.cs`
- `src/CodeMemory.AspNet/Configuration/StorageServiceRouter.cs`

---

## Task 2: Build SqlExpressionBuilder

### Priority

High

### Goal

Create `SqlExpressionBuilder` that walks a SqlParserCS WHERE AST and produces `Expression<Func<TRecord, bool>>` using `System.Linq.Expressions`.

### Why this exists

This is the core translation layer. Without it, the SQL WHERE clause cannot be applied to the InMemoryVectorStore's filter-based `GetAsync` method.

### Scope

- Create `src/CodeMemory.AspNet/SqlQuery/SqlExpressionBuilder.cs`
- Support these SQL operator → LINQ expression mappings:

| SQL | LINQ |
|---|---|
| `col = val` | `Expression.Equal` |
| `col <> val` | `Expression.NotEqual` |
| `col > val` | `Expression.GreaterThan` |
| `col < val` | `Expression.LessThan` |
| `col >= val` | `Expression.GreaterThanOrEqual` |
| `col <= val` | `Expression.LessThanOrEqual` |
| `col LIKE '%pat%'` | `string.Contains` / `StartsWith` / `EndsWith` |
| `col IN (a,b,c)` | `Enumerable.Contains` with inline array |
| `col IS NULL` | `Expression.Equal(prop, Constant(null))` |
| `col IS NOT NULL` | `Expression.NotEqual(prop, Constant(null))` |
| `col BETWEEN a AND b` | `a <= col && col <= b` |
| `cond1 AND cond2` | `Expression.AndAlso` |
| `cond1 OR cond2` | `Expression.OrElse` |
| `NOT cond` | `Expression.Not` |

- Handle type conversion: SQL literal type → target C# property type
  - SqlParserCS `Value.Number` → `int`, `long`, `double` (match target property)
  - `Value.SingleQuotedString` → `string`
  - `Value.Null` → `null`
  - `Value.Boolean` → `bool`
- Handle column name → `PropertyInfo` resolution via reflection on `TRecord`
- Handle `SELECT *` expansion to all readable properties
- Handle `SELECT col1, col2` to specific columns

### Constraints

- Must work with `SymbolRecord`, `ChunkRecord`, `RelationshipRecord` (and be generic enough for future types)
- Must not reference Memori types directly (keep the builder pure expression-tree work)
- Must use `System.Linq.Expressions` only — no external expression libraries

### Suggested implementation path

1. Start with `BuildFilter<TRecord>(Expression? whereClause)` returning `Expression<Func<TRecord, bool>>?`
2. For `null` WHERE, return `null` (match all)
3. Handle simple `BinaryOp(Eq, col, literal)` first, then compose AND/OR
4. Use a private `VisitExpression(Expression, ParameterExpression)` recursive method matching on pattern
5. Use `Expression.Convert()` when SQL literal type doesn't match target property type
6. Add LIKE handling via `string.Contains`/`StartsWith`/`EndsWith` MethodInfo cache

### Acceptance criteria

- `SqlExpressionBuilder.BuildFilter<SymbolRecord>(whereAst)` returns a valid `Expression<Func<SymbolRecord, bool>>`
- The compiled expression correctly filters in-memory records
- Type conversion works: SQL `'Class'` → string, SQL `10` → int, SQL `3.14` → double
- AND/OR nesting works correctly
- LIKE with `%pattern%`, `pattern%`, `%pattern` all work (StartsWith, EndsWith, Contains)
- IN clause works
- IS NULL / IS NOT NULL works
- BETWEEN works
- NOT works
- Unknown column names throw a descriptive error

### Files likely involved

- `src/CodeMemory.AspNet/SqlQuery/SqlExpressionBuilder.cs` (new file, ~250 lines)

---

## Task 3: Build CollectionResolver + SqlQueryService

### Priority

High

### Goal

Create `CollectionResolver` (maps table names to VectorStore collections) and `SqlQueryService` (orchestrates the full parse → build → query → materialize pipeline).

### Why this exists

These are the glue between the expression builder and the MCP tool. They handle collection resolution, result materialization, and the vector search path.

### Scope

- Create `src/CodeMemory.AspNet/SqlQuery/CollectionResolver.cs`
  - Map: `"SymbolRecord" → ("symbols", typeof(SymbolRecord))`
  - Map: `"ChunkRecord" → ("chunks", typeof(ChunkRecord))`
  - Map: `"RelationshipRecord" → ("relationships", typeof(RelationshipRecord))`
  - For a given table name + `VectorStore`, return an `InMemoryVectorStoreCollection<string, TRecord>`
  - Cache `MethodInfo` for generic `GetCollection<TKey, TRecord>` and `GetAsync(filter)` to avoid repeated reflection

- Create `src/CodeMemory.AspNet/SqlQuery/SqlQueryService.cs`
  - `ExecuteAsync(string sql, int maxResults)` → `SqlQueryResult`
  - Steps:
    1. Parse SQL with `new SqlQueryParser().Parse(sql)`
    2. Validate: exactly one SELECT statement
    3. Extract table name from FROM clause → resolve via `CollectionResolver`
    4. If `ORDER BY Similarity DESC` detected → vector search path:
       a. Extract query text from WHERE (LIKE condition on Content)
       b. Generate embedding via `IEmbeddingGenerator`
       c. Call `collection.SearchAsync(embedding, top, options with filter)`
    5. Else → filter path:
       a. Call `SqlExpressionBuilder.BuildFilter<TRecord>(whereClause)`
       b. Call `collection.GetAsync(filter, maxResults)`
    6. Materialize each record to `Dictionary<string, object?>` (all properties, or selected columns)
    7. Apply ORDER BY client-side if not handled by the store
    8. Return result with row count and execution time

- Create `src/CodeMemory.AspNet/SqlQuery/SqlQueryResult.cs`
  ```csharp
  public sealed record SqlQueryResult(
      bool Success,
      long RowCount,
      long ExecutionTimeMs,
      List<string>? Columns,
      List<Dictionary<string, object?>>? Rows,
      string? Error = null
  );
  ```

### Constraints

- Must resolve the per-repo `VectorStore` via `IStorageService` + `IRepoContextAccessor`
- Must handle both InMemoryVectorStore (Memori) and return clear error for other backends
- Must use `IEmbeddingGenerator<string, Embedding<float>>` from DI for vector search path
- All methods must accept `CancellationToken`

### Acceptance criteria

- `CollectionResolver` resolves all 3 table names correctly
- `SqlQueryService.ExecuteAsync` parses SQL and returns results for valid queries
- Vector search path works: detects `ORDER BY Similarity DESC` and uses embedding
- Clear error returned for:
  - Unknown table name
  - Non-SELECT statements
  - Backend is not InMemoryVectorStore
  - Parse errors from SqlParserCS
- Execution time is measured and returned

### Files likely involved

- `src/CodeMemory.AspNet/SqlQuery/CollectionResolver.cs` (new file, ~100 lines)
- `src/CodeMemory.AspNet/SqlQuery/SqlQueryService.cs` (new file, ~200 lines)
- `src/CodeMemory.AspNet/SqlQuery/SqlQueryResult.cs` (new file, ~15 lines)

---

## Task 4: Replace SqlQueryTool + Wire up DI

### Priority

High

### Goal

Replace the current LiteGraph-copy `SqlQueryTool` with a real SQL MCP tool. Register `SqlQueryService` and `CollectionResolver` in DI.

### Why this exists

The current `SqlQueryTool` is a misnamed copy of `GraphQueryTool` that still calls LiteGraph. It must be replaced to expose actual SQL querying. The new services need DI registration.

### Scope

- Replace entire `SqlQueryTool.cs` with:
  - `[McpServerToolType]` class
  - Constructor takes `SqlQueryService`, `ILogger<SqlQueryTool>`
  - `[McpServerTool] SqlQueryAsync(string query, int maxResults = 100)`
  - Description: `"Execute SQL queries against the indexed repository data. Supports SELECT with WHERE, ORDER BY, LIMIT. Tables: SymbolRecord, ChunkRecord, RelationshipRecord. Use ORDER BY Similarity DESC for vector search on ChunkRecord."`
  - Returns `IDictionary<string, object?>` with success, rowCount, executionTimeMs, columns, rows
  - Error handling: parse errors, execution errors

- Add DI registration in `Program.cs`:
  ```csharp
  builder.Services.AddSingleton<CollectionResolver>();
  builder.Services.AddSingleton<SqlQueryService>();
  ```

- Create `src/CodeMemory.AspNet/SqlQuery/TableSchemaProvider.cs`:
  - `GetTableSchema(string tableName) → List<ColumnInfo>`
  - `GetAllTableSchemas() → Dictionary<string, List<ColumnInfo>>`
  - `ColumnInfo` has: `Name`, `Type`, `IsNullable`, `IsKey`, `IsVector`
  - Used in tool description to inform LLMs

### Constraints

- Must NOT modify or remove `GraphQueryTool.cs` (leave it for A/B comparison)
- Must handle `StorageServiceRouter` for multi-repo (current `StorageServiceRouter.GetStorage()` pattern)
- The tool file stays at `src/CodeMemory.AspNet/Tools/SqlQueryTool.cs`
- Tool description must include example queries so LLMs can use it without trial-and-error

### Acceptance criteria

- `SqlQueryTool.SqlQueryAsync` runs an actual SQL query against the in-memory store
- Simple query works: `SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 5`
- Column-filtered query works: `SELECT Id, Name FROM SymbolRecord WHERE Kind = 'Method'`
- Vector search works: `SELECT * FROM ChunkRecord WHERE Content LIKE '%auth%' ORDER BY Similarity DESC LIMIT 3`
- Clear error for: unknown table, parse error, LiteGraph backend
- `TableSchemaProvider.GetAllTableSchemas()` returns metadata for all 3 tables
- Build succeeds with `dotnet build src/CodeMemory.AspNet/CodeMemory.AspNet.csproj`

### Files likely involved

- `src/CodeMemory.AspNet/Tools/SqlQueryTool.cs` (replaced, ~60 lines)
- `src/CodeMemory.AspNet/Program.cs` (+3 lines for DI registration)
- `src/CodeMemory.AspNet/SqlQuery/TableSchemaProvider.cs` (new file, ~80 lines)

---

## Task 5: Self-Test Against the CodeMemory Repo Index

### Priority

Medium

### Goal

Verify the SQL surface works end-to-end against a real CodeMemory index of its own repository. Document sample queries and their results.

### Why this exists

Unit tests can verify the expression builder in isolation, but the real value is confirmed only when the MCP tool runs against an indexed repo. This task validates the full pipeline end-to-end.

### Scope

- Start the AspNet host with `Storage:Provider: "inmemory"` and a repo pointing at the CodeMemory repo
- Let indexing complete (poll `ping` until `indexingCompleted: true`)
- Execute these SQL queries via the MCP endpoint and verify results:
  ```sql
  -- 1. List all classes
  SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 5

  -- 2. Find methods in a specific file
  SELECT Id, Name, LineStart, LineEnd FROM SymbolRecord
    WHERE Kind = 'Method' AND FilePath LIKE '%SqlQueryService%'

  -- 3. Find all relationships of type 'Calls'
  SELECT SourceSymbolId, TargetSymbolId FROM RelationshipRecord
    WHERE RelationshipType = 'Calls' LIMIT 10

  -- 4. Vector search for authentication-related chunks
  SELECT FilePath, Content FROM ChunkRecord
    WHERE Content LIKE '%authentication%'
    ORDER BY Similarity DESC LIMIT 3

  -- 5. Find public methods
  SELECT Name, FilePath FROM SymbolRecord
    WHERE Kind = 'Method' AND Modifiers LIKE '%public%' LIMIT 10

  -- 6. Complex filter
  SELECT Name, FilePath, LineStart FROM SymbolRecord
    WHERE Kind IN ('Class', 'Interface') AND LineStart > 0
    ORDER BY Name LIMIT 5
  ```

- Verify each returns non-empty results with correct columns
- Check error messages for invalid queries

### Acceptance criteria

- All 6 sample queries return correct, non-empty results
- Vector search returns semantically relevant chunks
- Error queries return structured error responses
- Execution times are reasonable (< 2s for simple queries)
- Document sample outputs in this task file or a companion gist

### Files likely involved

- `src/CodeMemory.AspNet/Program.cs` (used only to run the host)
- No code changes expected — this is a validation task

### Self-Test Results (2026-05-15)

Ran all 6 queries against CodeMemory's own index (InMemoryVectorStore, NgramEmbeddingGenerator).

**Query 1 — List classes**
```sql
SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 5
```
✅ 5 rows, 66ms
```
TreeSitterParser    | ../src/CodeMemory/Indexing/Parsing/TreeSitterParser.cs
SymbolRecord        | ../src/CodeMemory/Storage/Models.cs
VectorSchema        | ../src/CodeMemory/Storage/VectorSchema.cs
LanguageDetector    | ../src/CodeMemory/Indexing/Parsing/LanguageDetector.cs
GenericMethodClass  | ../src/CodeMemory.Tests/fixtures/SampleClass.cs
```

**Query 2 — Methods in SqlQueryService file**
```sql
SELECT Id, Name, LineStart, LineEnd FROM SymbolRecord
  WHERE Kind = 'Method' AND FilePath LIKE '%SqlQueryService%'
```
✅ 16 rows, 23ms
```
FindGetAsyncFilterMethod        (line 198-205)
Fail                            (line 417-421)
ExtractTableName                (line 278-283)
QueryVectorAsync                (line 136-196)
ExtractVectorSearchText         (line 223-254)
SqlQueryService                 (line 25-33)
BuildFilterExpression           (line 207-215)
ToAsyncEnumerable               (line 367-415)
MaterializeAsync                (line 285-349)
ExtractCleanText                (line 262-268)
QueryFilteredAsync              (line 114-134)
IsVectorSearch                  (line 270-276)
IsColumnReference               (line 256-260)
ExecuteAsync                    (line 35-112)
MakeTrueExpression              (line 217-221)
RecordToDictionary              (line 351-365)
```

**Query 3 — Calls relationships**
```sql
SELECT SourceSymbolId, TargetSymbolId FROM RelationshipRecord
  WHERE RelationshipType = 'Calls' LIMIT 10
```
✅ 0 rows (no relationships indexed in this repo — expected)

**Query 4 — Vector search for authentication**
```sql
SELECT FilePath, Content FROM ChunkRecord
  WHERE Content LIKE '%authentication%'
  ORDER BY Similarity DESC LIMIT 3
```
✅ 3 rows, 128ms (required `MakeGenericMethod` fix for `SearchAsync<T>`)
```
...\StorageService.cs:            "public async Task<RelationshipRecord?> GetRelationshipAsync..."
...\StorageService.cs:            "public async Task StoreRelationshipsAsync(...)"
...\RelationshipQueryService.cs:  "public Task<RelationshipRecord?> GetByIdAsync(...)"
```

**Query 5 — Public methods**
```sql
SELECT Name, FilePath FROM SymbolRecord
  WHERE Kind = 'Method' AND Modifiers LIKE '%public%' LIMIT 10
```
✅ 10 rows, 1ms (required null-guard fix for nullable `Modifiers` column)
```
GetSymbolsByFileAsync_ReturnsMatchingSymbols        | StorageServiceTests.cs
GetRelationshipsByTargetAsync                       | StorageServiceRouter.cs
WalkAsync                                           | FileCrawler.cs
AddCodeMemorySqlliteStorage(...)                    | ServiceCollectionExtensions.cs
SearchAsync(...)                                    | SemanticSearchService.cs
TreeSitterRelationshipExtractor(...)                | TreeSitterRelationshipExtractor.cs
StoreSymbolsAsync_And_GetSymbolAsync_RoundTrip      | StorageServiceTests.cs
GetEditContext_ReturnsFullContext_WithMocks          | EditContextToolTests.cs
GetChunksBySymbolAsync                              | StorageServiceRouter.cs
SqlQueryTool(...)                                   | SqlQueryTool.cs
```

**Query 6 — Complex filter**
```sql
SELECT Name, FilePath, LineStart FROM SymbolRecord
  WHERE Kind IN ('Class', 'Interface') AND LineStart > 0
  ORDER BY Name LIMIT 5
```
✅ 5 rows, 20ms
```
TreeSitterParser         | TreeSitterParser.cs        (line 6)
SymbolRecord             | Models.cs                  (line 26)
VectorSchema             | VectorSchema.cs            (line 5)
IDependencyGraphService  | IDependencyGraphService.cs (line 21)
LanguageDetector         | LanguageDetector.cs        (line 12)
```

**Bugs Found & Fixed During Self-Test**
1. `SearchAsync` is an open generic method — required `MakeGenericMethod(typeof(ReadOnlyMemory<float>))` before `Invoke`
2. `VisitLike`/`VisitILike` didn't guard against null column values (e.g. `Modifiers` is `string?`) — added `AndAlso(isNotNull, containsCall)` wrapper
3. `QueryFilteredAsync` ignored `ORDER BY` — added post-materialization sort

**Unit Tests Added**
- `src/CodeMemory.Tests/Services/Query/SqlQueryServiceTests.cs` — 10 tests covering all query patterns, vector search, error handling, and nullable column guards

---

## Suggested Agent Handoff Batches

### Batch A: infrastructure (Task 1)

One-liner property addition across 4 files. Fast, safe, low risk.

### Batch B: core translation (Tasks 2 → 3)

Dependency chain: Task 2 first, then Task 3. These are the meat of the feature.

### Batch C: integration (Task 4)

After Tasks 1-3 are complete, wire everything together. This creates the user-facing tool.

### Batch D: validation (Task 5)

Run against real data. Only start after Task 4 is merged and built successfully.

---

## Final Checklist

- [x] every task has a clear owner-sized scope
- [x] every task has acceptance criteria
- [x] decision-gate tasks are clearly marked (none here — all implementation)
- [x] likely files are listed to reduce agent search time
- [x] execution order reflects real dependencies
