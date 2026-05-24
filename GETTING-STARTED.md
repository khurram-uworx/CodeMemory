# Getting Started — CodeMemory STDIO MCP

Install `@uworx/code-memory` and configure it as an MCP tool for your agent:

```json
"code-memory": {
  "command": "npx",
  "args": ["-y", "@uworx/code-memory", "--repo", "/path/to/your/project"]
}
```

Indexing is non-blocking. **Poll `ping` until `indexingCompleted: true`** before calling any other tool.

> Full MCP tool reference: `tools/list`. Detailed architecture: [`ARCHITECTURE.md`](ARCHITECTURE.md). Agent constraints: [`AGENTS.md`](AGENTS.md).

---

## `sql_query` — Query Code Like a Database

Indexed code is exposed as three tables: `SymbolRecord`, `ChunkRecord`, `RelationshipRecord`. Full SQL surface: SELECT, WHERE (including `IN (SELECT ...)` subqueries), ORDER BY, GROUP BY, HAVING, DISTINCT, aggregates (COUNT/SUM/AVG/MIN/MAX), CTEs (non-recursive, chained), derived tables, INNER/LEFT/RIGHT/FULL OUTER/CROSS JOINs, `USING(col)` shorthand, `UNION`/`INTERSECT`/`EXCEPT`, vector search (`ORDER BY Similarity DESC`).

### Repository Exploration

| Intent | Query |
|--------|-------|
| What kinds of symbols exist? | `SELECT DISTINCT Kind FROM SymbolRecord` |
| All interfaces | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Interface' ORDER BY Name` |
| Largest classes by line count | `SELECT Name, FilePath, (LineEnd - LineStart) AS Lines FROM SymbolRecord WHERE Kind = 'Class' ORDER BY Lines DESC LIMIT 10` |
| Static async methods | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Method' AND Modifiers LIKE '%static%' AND Modifiers LIKE '%async%'` |
| Find records and structs | `SELECT Name, FilePath, Kind FROM SymbolRecord WHERE Kind IN ('Record', 'Struct')` |

### Debugging & Navigation

| Intent | Query |
|--------|-------|
| All methods in StorageService files | `SELECT Name, LineStart, LineEnd FROM SymbolRecord WHERE FilePath LIKE '%StorageService%' AND Kind = 'Method' ORDER BY LineStart` |
| Search for error-handling code (vector ranked) | `SELECT FilePath, Content FROM ChunkRecord WHERE Content ILIKE '%exception%' OR Content ILIKE '%error%' ORDER BY Similarity DESC LIMIT 5` |
| Classes without explicit modifiers | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Class' AND Modifiers IS NULL` |
| Content search in markdown/text files | `SELECT FilePath, Content FROM ChunkRecord WHERE Language = 'Text' AND Content ILIKE '%getting started%'` |

### Architecture Analysis

| Intent | Query |
|--------|-------|
| Count symbols per file (most symbols) | `SELECT FilePath, COUNT(*) AS SymbolCount FROM SymbolRecord GROUP BY FilePath ORDER BY SymbolCount DESC LIMIT 20` |
| Public API surface | `SELECT Name, Kind FROM SymbolRecord WHERE Modifiers ILIKE '%public%' AND Kind IN ('Class', 'Interface', 'Record') ORDER BY Kind, Name` |
| Test-coverage gaps (Services classes not in Tests) | `SELECT Name, FilePath FROM SymbolRecord WHERE Kind = 'Class' AND FilePath NOT LIKE '%Test%' AND FilePath LIKE '%Services%' ORDER BY Name` |
| Find DI registrations | `SELECT DISTINCT FilePath FROM ChunkRecord WHERE Content LIKE '%AddSingleton%' OR Content LIKE '%AddScoped%'` |
| Most referenced classes | `SELECT s.Name, COUNT(*) AS RefCount FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.TargetSymbolId WHERE r.RelationshipType = 'References' GROUP BY s.Name ORDER BY RefCount DESC LIMIT 10` |
| Files with the most public methods | `SELECT FilePath, COUNT(*) AS Count FROM SymbolRecord WHERE Kind = 'Method' AND Modifiers LIKE '%public%' GROUP BY FilePath HAVING Count > 5 ORDER BY Count DESC LIMIT 10` |
| Components with cross-component coupling | `WITH comps AS (SELECT DISTINCT Name, FilePath FROM SymbolRecord WHERE Kind IN ('Class','Interface')) SELECT c.Name, COUNT(*) AS deps FROM comps c JOIN RelationshipRecord r ON r.SourceSymbolId = c.Name GROUP BY c.Name HAVING deps > 3 ORDER BY deps DESC` |

### Vector Search

`ORDER BY Similarity DESC` on `ChunkRecord` requires `Content LIKE` (exact substring filter), then re-ranks results by embedding similarity. Each row includes `__score` (0-1).

```sql
-- Find most semantically relevant auth-related code
SELECT FilePath, Content FROM ChunkRecord WHERE Content LIKE '%auth%' ORDER BY Similarity DESC LIMIT 3

-- Broader filter with vector ranking
SELECT FilePath, Content FROM ChunkRecord WHERE Content LIKE '%database%' OR Content LIKE '%cache%' ORDER BY Similarity DESC LIMIT 5
```

### Constraints

| Construct | Behavior |
|-----------|----------|
| Column aliases (`AS`) | Supported — result uses alias name as column header |
| Modifiers field | Comma-separated string — use `LIKE '%public%'`, never `=` |
| `RelationshipRecord` | May be empty — depends on extraction phase |
| `ORDER BY Similarity DESC` | Requires `Content LIKE` in WHERE — cannot rank unfiltered results |
