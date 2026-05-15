# SQL Query Tool — Field-Testing Guide

## Category 1: Onboarding / Repo Exploration

### What's in this codebase?

| Scenario | Query |
|---------|-------|
| What kinds of symbols exist? | `SELECT DISTINCT Kind FROM SymbolRecord` |
| Find all interfaces | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Interface' ORDER BY Name` |
| Show all static/async methods | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Method' AND Modifiers LIKE '%static%' AND Modifiers LIKE '%async%'` |
| What files are most complex? | `SELECT Name, FilePath, (LineEnd - LineStart) AS Lines FROM SymbolRecord WHERE Kind = 'Class' ORDER BY Lines DESC LIMIT 10` |
| Find records/structs | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind IN ('Record', 'Struct')` |

---

## Category 2: Debugging / Find the Code

### Where is X implemented?

| Scenario | Query |
|---------|-------|
| Find all methods in a specific file | `SELECT Name, LineStart, LineEnd FROM SymbolRecord WHERE FilePath LIKE '%StorageService%' AND Kind = 'Method' ORDER BY LineStart` |
| Find nested types | `SELECT Name, Kind FROM SymbolRecord WHERE Name LIKE '%Inner%' OR FullName LIKE '%.%'` |
| Find classes without explicit modifiers | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Class' AND Modifiers IS NULL` |
| Search for error handling | `SELECT FilePath, Content FROM ChunkRecord WHERE Content ILIKE '%exception%' OR Content ILIKE '%error%' ORDER BY Similarity DESC LIMIT 5` |

---

## Category 3: Architecture / Cross-Cutting

### What patterns exist across the codebase?

| Scenario | Query |
|---------|-------|
| Find test-coverage gaps | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Class' AND FilePath NOT LIKE '%Test%' AND FilePath LIKE '%Services%'` |
| Find all records with documentation | `SELECT Name, FilePath FROM SymbolRecord WHERE Documentation IS NOT NULL ORDER BY Name` |
| List all public API surface | `SELECT Name FROM SymbolRecord WHERE Modifiers ILIKE '%public%' AND Kind IN ('Class', 'Interface', 'Record') ORDER BY Name` |
| Find DI registrations | `SELECT FilePath FROM ChunkRecord WHERE Content LIKE '%AddSingleton%' OR Content LIKE '%AddScoped%' OR Content LIKE '%AddTransient%'` |

---

## Category 4: Edge Cases / Known Behaviors

### Things that will help your colleagues understand the tool's limits

| Test | What It Checks |
|------|----------------|
| `SELECT Name FROM SymbolRecord LIMIT 5` | No `WHERE` clause — verifies empty `WHERE` returns all |
| `SELECT * FROM NonExistent` | Error: `"Unknown table"` |
| `INSERT INTO SymbolRecord VALUES (1)` | Error: `"Only SELECT"` — verifies DML rejection |
| `SELECT * FROM RelationshipRecord LIMIT 5` | `RelationshipRecord` may be empty — verify graceful handling |
| `SELECT Name AS MyName FROM SymbolRecord LIMIT 3` | Alias is ignored — verify column names stay as `Name` |
| `Modifiers LIKE '%PUBLIC%'` vs `ILIKE '%public%'` | Case sensitivity difference |


## Things to warn colleagues about before field-testing:
1. No COUNT/aggregates — SELECT COUNT(*) won't work, it's SELECT-only
2. No JOINs — single-table queries only
3. No aliases — AS is parsed but ignored
4. No DISTINCT — parsed but ignored (we saw 50 non-unique rows for a DISTINCT query)
5. ORDER BY on large sets — client-side sorted, so ordering 1000+ rows will be slow
6. RelationshipRecord may be empty — depends on the repo's extraction phase
7. Modifiers are comma-separated — search with LIKE '%public%', not = 'public'

## Purpose

Real-world scenarios for testing the `sql_query` MCP tool against a real repo index. Each scenario represents a coding task where `sql_query` is the **right tool** — multi-column filters, combined conditions, vector search with WHERE constraints, or ad-hoc exploration that no other code-memory tool handles.

These are not unit tests (see `src/CodeMemory.Tests/Services/Query/SqlQueryServiceTests.cs` for those). These are end-to-end smoke tests to validate the tool surfaces correct, useful results when an LLM agent calls it during real work.

---

## Prerequisites

- CodeMemory.AspNet server running (`Storage:Provider: "inmemory"`)
- Indexing complete (`ping` returns `indexingCompleted: true`)
- The repo being tested has been indexed (default `codememory` repo for self-test, or a real project repo)

---

## Category 1: Onboarding / Repo Exploration

"What's in this codebase?"

### 1.1 List all symbol kinds

```sql
SELECT Name FROM SymbolRecord WHERE Kind = 'Enum' ORDER BY Name
```

**Why this tool:** No other code-memory tool lists all symbol types by kind. `get_architecture_overview` gives file counts, not symbol-level breakdowns.

**Expect:** All enums in the repo, sorted alphabetically.

---

### 1.2 Find all interfaces

```sql
SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Interface' ORDER BY Name
```

**Why this tool:** `find_related_code` requires a starting symbol — you can't list all interfaces without one. `sql_query` gives you the full list in one call.

---

### 1.3 Cross-filter on modifier combinations

```sql
SELECT Name, FilePath, LineStart FROM SymbolRecord
  WHERE Kind = 'Method'
    AND Modifiers LIKE '%static%'
    AND Modifiers LIKE '%async%'
  ORDER BY Name
```

**Why this tool:** No other tool lets you combine `Kind` + multiple `Modifiers` filters. This returns static async methods — impossible with `get_edit_context`, `find_related_code`, or `trace_dependency`.

**Known behavior:** Modifiers are comma-separated strings (e.g. `"public,static,async"`). Always use `LIKE` with `%` wildcards, never `=`.

---

### 1.4 File complexity by line count

```sql
SELECT Name, FilePath, (LineEnd - LineStart) AS Lines
  FROM SymbolRecord WHERE Kind = 'Class'
  ORDER BY (LineEnd - LineStart) DESC LIMIT 10
```

**Note:** `AS` aliases are parsed but ignored — the column name in results will be `Lines`, not the expression.

---

### 1.5 Find records and structs

```sql
SELECT Name, FilePath, Kind FROM SymbolRecord
  WHERE Kind IN ('Record', 'Struct')
```

---

## Category 2: Debugging / Find the Code

"Where is X implemented?"

### 2.1 All methods in a specific file

```sql
SELECT Name, LineStart, LineEnd FROM SymbolRecord
  WHERE FilePath LIKE '%StorageService%' AND Kind = 'Method'
  ORDER BY LineStart
```

**Why this tool:** `get_edit_context` works by symbol name, not by file path. To find everything in a file, you need `sql_query`'s `FilePath LIKE` + `Kind` + `ORDER BY LineStart` combo.

---

### 2.2 Find nested/inner types

```sql
SELECT Name, Kind, FullName FROM SymbolRecord
  WHERE FullName LIKE '%.%' AND Kind IN ('Class', 'Enum')
  ORDER BY FullName
```

---

### 2.3 Classes without explicit modifiers

```sql
SELECT Name, FilePath FROM SymbolRecord
  WHERE Kind = 'Class' AND Modifiers IS NULL
```

**Why this tool:** Tests the nullable-column guard fix. Also genuinely useful — finds internal/default-scope classes.

---

### 2.4 Case-insensitive content search with vector ranking

```sql
SELECT FilePath, Content FROM ChunkRecord
  WHERE Content ILIKE '%exception%' OR Content ILIKE '%error%'
  ORDER BY Similarity DESC LIMIT 5
```

**Why this tool:** `ILIKE` + `ORDER BY Similarity DESC` is unique. `semantic_search` does fuzzy embedding search, but can't combine with a text filter. This is exact ILIKE match + embedding ranking.

---

## Category 3: Architecture / Cross-Cutting

"What patterns exist across the codebase?"

### 3.1 Test-coverage gaps — source classes not in test files

```sql
SELECT Name, FilePath FROM SymbolRecord
  WHERE Kind = 'Class'
    AND FilePath NOT LIKE '%Test%'
    AND FilePath LIKE '%Services%'
  ORDER BY Name
```

**Why this tool:** Cross-source/tests filter. No other tool lets you combine `FilePath NOT LIKE '%Test%'` with `FilePath LIKE '%Services%'`.

---

### 3.2 Symbols with documentation comments

```sql
SELECT Name, Kind, FilePath FROM SymbolRecord
  WHERE Documentation IS NOT NULL
  ORDER BY Kind, Name
```

---

### 3.3 Public API surface

```sql
SELECT Name, Kind FROM SymbolRecord
  WHERE Modifiers ILIKE '%public%'
    AND Kind IN ('Class', 'Interface', 'Record')
  ORDER BY Kind, Name
```

---

### 3.4 Find DI registrations across chunks

```sql
SELECT FilePath, Content FROM ChunkRecord
  WHERE Content LIKE '%AddSingleton%'
     OR Content LIKE '%AddScoped%'
     OR Content LIKE '%AddTransient%'
  ORDER BY FilePath
```

**Why this tool:** Searches chunk content with OR conditions. `semantic_search` can't do string-precise matching.

---

## Category 4: Vector Search

### 4.1 Basic vector search on authentication-related code

```sql
SELECT FilePath, Content FROM ChunkRecord
  WHERE Content LIKE '%auth%'
  ORDER BY Similarity DESC LIMIT 3
```

**Expect:** Results include a `__score` field (0-1) in each row. The top result should be the most semantically relevant.

**Note:** The `Content LIKE '%auth%'` filter runs first (exact substring match on chunk text), then results are ranked by embedding similarity to the LIKE pattern text.

---

### 4.2 Vector search with broader filter

```sql
SELECT FilePath, Content, Language FROM ChunkRecord
  WHERE Content LIKE '%database%' OR Content LIKE '%cache%'
  ORDER BY Similarity DESC LIMIT 5
```

**Expect:** `__score` present in each result row. With OR conditions, the remaining filter combines both patterns.

---

## Category 5: Edge Cases & Error Handling

Test these to understand boundaries:

### 5.1 No WHERE clause — returns all rows with LIMIT

```sql
SELECT Name FROM SymbolRecord LIMIT 5
```

### 5.2 Unknown table — clear error expected

```sql
SELECT * FROM NonExistentTable
```

**Expect:** `{"success": false, "error": "Unknown table 'NonExistentTable'. Available: SymbolRecord, ChunkRecord, RelationshipRecord"}`

---

### 5.3 Non-SELECT statement — rejection

```sql
INSERT INTO SymbolRecord VALUES (1)
```

**Expect:** `{"success": false, "error": "Only SELECT statements are supported"}`

---

### 5.4 Relationship table may be empty

```sql
SELECT * FROM RelationshipRecord LIMIT 5
```

**Expect:** 0 rows if the repo's extraction phase didn't populate relationships. This is expected — depends on the extraction pipeline, not the query tool.

---

### 5.5 Column alias is parsed but ignored

```sql
SELECT Name AS MyName FROM SymbolRecord WHERE Kind = 'Class' LIMIT 3
```

**Expect:** Column name in results is `Name`, not `MyName`.

---

### 5.6 Case sensitivity: LIKE vs ILIKE

```sql
SELECT Name FROM SymbolRecord WHERE Kind = 'Method' AND Modifiers LIKE '%PUBLIC%' LIMIT 3
SELECT Name FROM SymbolRecord WHERE Kind = 'Method' AND Modifiers ILIKE '%public%' LIMIT 3
```

**Expect:** First query returns 0 rows (LIKE is case-sensitive for the pattern). Second query returns public methods (ILIKE is case-insensitive).

---

## Known Limitations

| Construct | Status | Note |
|-----------|--------|------|
| `SELECT DISTINCT` | Parsed but ignored | Returns all matching rows, not distinct |
| `COUNT(*)`, `SUM`, `GROUP BY` | Not supported | Single-table SELECT only |
| `JOIN`, subqueries | Not supported | Single-table only |
| Column aliases (`AS`) | Parsed but ignored | Result uses original column name |
| `ORDER BY` on 1000+ rows | Works but slow | Client-side sorting after fetch |
| `RelationshipRecord` | May be empty | Depends on extraction phase |
| Backend other than InMemoryVectorStore | Error | Returns clear "not supported" message |
| SQL injection | Not applicable | Tool takes structured params, not raw queries |

---

## How to Run These

Through your MCP client, call:

```json
{
  "name": "sql_query",
  "arguments": {
    "query": "SELECT Name FROM SymbolRecord WHERE Kind = 'Enum' ORDER BY Name",
    "maxResults": 20
  }
}
```

Or via PowerShell against the raw MCP endpoint:

```powershell
$headers = @{ "Accept" = "application/json, text/event-stream"; "Content-Type" = "application/json" }
$body = @{
  jsonrpc = "2.0"
  id = [guid]::NewGuid().ToString()
  method = "tools/call"
  params = @{
    name = "sql_query"
    arguments = @{ query = "SELECT Name FROM SymbolRecord WHERE Kind = 'Class' LIMIT 5"; maxResults = 10 }
  }
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri "http://localhost:4792/api/mcp/codememory" -Method Post -Headers $headers -Body $body
```
