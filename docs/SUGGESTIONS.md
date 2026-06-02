# MCP Tool Improvements

Observations from a coding agent using code-memory over a session:

- `grep` / text search was better than the symbol graph for: *"find all services that inject IStorageService, and tell me which of its members each one calls"*
- Code-memory's symbol-relationship index excels at architecture-level queries (dependency chains, impact analysis, component clusters) but has no concept of "interface member usage" — who calls what on a given type

## 1. New tool: `find_type_usages`

**What:** Given a fully-qualified type/interface name (e.g. `CodeMemory.Storage.IStorageService`), return all classes that reference it and, for each consumer, **which specific members** they access (`.RepoRoot`, `.StoreSymbolsAsync`, `.GetSymbolAsync`, etc.), grouped by member.

**Why:** Answers the question *"what would break if I changed this interface?"* without reading N files manually.

**Implementation sketch:** Post-process the existing chunk index (no re-indexing needed). For each chunk whose content references the target type, extract `identifier.MemberName` call patterns via regex. Works offline, no new extractor or relationship type needed.

**Workflow:**
```sql
-- Candidate consumers via existing chunk search
SELECT FilePath FROM ChunkRecord WHERE Content ILIKE '%IStorageService%'
-- Then post-process each chunk for field access / member call patterns
```

## 2. Enhanced extraction: `member_access` relationship type

**What:** Add a new relationship type `member_access` to the existing symbol extractor:
```
ConsumerSymbol --member_access--> InterfaceOrType.MemberName
```
E.g., `GitHistoryService --member_access--> IStorageService.RepoRoot` and `GitHistoryService --member_access--> IStorageService.GetSymbolByFullNameAsync`.

**Why:** Makes usage queries flow through the standard symbol/relationship graph — `trace_dependency`, `find_related_code`, and `sql_query` on `RelationshipRecord` would all support it without new tools.

**Tradeoff:** Requires updating the Roslyn extractors to emit these edges (they already parse `MemberAccessExpressionSyntax` nodes for `calls` relations — this is a natural extension) and re-indexing all repos.

## 3. SQL engine: regex search on ChunkRecord

**What:** Add a `REGEX_MATCH(content, pattern)` predicate to `sql_query`:
```sql
SELECT FilePath, Content FROM ChunkRecord
WHERE Content REGEX_MATCH 'storage\.RepoRoot|storage\.StoreSymbols'
```

**Why:** The existing `ILIKE` can't distinguish `storage.RepoRoot` from `storage.StoreSymbolsAsync` in a single query, and can't handle disjunctions. Regex makes ad-hoc usage analysis trivially composable from the SQL tool.

**Tradeoff:** Simpler than #2 (no extraction changes, no re-indexing), but requires regex support in the SQL execution engine and is limited to text matching — no symbol-level precision.
