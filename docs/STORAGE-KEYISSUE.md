# Storage Key Strategy — GUID + FullName Lookup

## Problem

`SymbolRecord.Id` was originally set to `Symbol.FullName`. Qualified names like `Namespace.Outer.Inner.Class.Method(System.String, System.Int32)` can far exceed key length limits of certain backends (SQL Server 900 bytes, MySQL 767 bytes).

`Id` is `[VectorStoreKey]` and used as the primary key in both vector stores (`InMemoryVectorStore`, SQLite via `memori`) and relational stores (`HybridStorageService`).

`ChunkRecord.SymbolId`, `RelationshipRecord.SourceSymbolId`, and `RelationshipRecord.TargetSymbolId` reference symbols by their `Id`.

---

## Current Solution — GUID + FullName Lookup

### Approach

- **Write path**: Each symbol gets `Guid.NewGuid().ToString("N")` (32 hex chars, no hyphens) as its `Id`. A `Dictionary<string, string>` (FullName → GUID) is built in-memory during indexing to resolve relationship source/target references from FullNames to GUIDs.
- **Read path**: Service entry points receive a `symbolPath` (FullName) from MCP tools. They look up the symbol via `GetSymbolByFullNameAsync(fullName)`, which queries the stored `FullName` data field. The returned GUID is then used for relationship/chunk queries.
- **No hashing**: The `SymbolIdHasher` was removed entirely.

### Rationale

Indexing is **one-pass** — every symbol discovered by Roslyn/TreeSitter is physically unique per file. Two identical `print()` functions in `a.js` and `b.js` are genuinely different symbols. A random GUID is:
- Simpler and faster than SHA256 hashing
- Zero collision concern
- Fixed 32-char key fits any backend

### Files Changed

| File | Change |
|---|---|
| **DELETED** `src/CodeMemory/Storage/SymbolIdHasher.cs` | Removed — no more hashing |
| `src/CodeMemory/Storage/IStorageService.cs` | Added `GetSymbolByFullNameAsync(string fullName, ...)` |
| `src/CodeMemory/Storage/StorageService.cs` | Implemented FullName lookup via LINQ filter |
| `src/CodeMemory.AspNet/Storage/HybridStorageService.cs` | Implemented FullName lookup via EF Core query |
| `src/CodeMemory.AspNet/Configuration/StorageServiceRouter.cs` | Delegates FullName lookup to per-repo storage |
| `src/CodeMemory/Services/IndexingEngine.cs` | GUID generation, FullName→Guid mapping in `mapToSymbolRecord`, `mapToChunkRecord`, `mapToRelationshipRecord` |
| `src/CodeMemory/Mcp/Services/EditContextService.cs` | `GetSymbolByFullNameAsync(symbolPath)` instead of hash |
| `src/CodeMemory/Services/Git/GitHistoryService.cs` | `GetSymbolByFullNameAsync(symbolPath)` instead of hash |
| `src/CodeMemory/Services/Graph/DependencyGraphService.cs` | FullName lookup at all 3 entry points, pass GUID to BFS/storage |
| Test files (3) | Removed hash usage, use GUIDs + FullName in test data |

### Key Flow

```
Indexing:
  Symbol(FullName="MyClass") → SymbolRecord.Id = "a1b2c3..." (GUID)
  Relationship(Source=FullName, Target=FullName)
    → RelationshipRecord.SourceSymbolId = GUID lookup from FullName→Guid map
    → RelationshipRecord.TargetSymbolId = GUID lookup from FullName→Guid map

Lookup:
  agent sends "MyClass" (FullName)
    → GetSymbolByFullNameAsync("MyClass")
    → query WHERE FullName = "MyClass"
    → returns SymbolRecord with Id="a1b2c3..."
    → use Id for relationship/chunk queries
```

### Design Decisions

- `RelationshipRecord.Id` keeps the composite format `$"{guidSource}->{guidTarget}:{type}"` for deterministic dedup
- `GetSymbolByFullNameAsync` returns `FirstOrDefault()` — same FullName in different files returns first match; acceptable for single-repo use
- `DependencyNode.SymbolName` contains GUIDs (opaque). Resolvable via `GetSymbolAsync(GUID)` → FullName if needed for display
- MCP tools unchanged — `symbolPath` parameter still accepts FullName

### What Did NOT Change

- `Models.cs` — `Id` is still `string` with `[VectorStoreKey]`
- `StorageService.cs` — collection types unchanged (`GetCollection<string, ...>`)
- MCP tool classes (5) — no changes; `symbolPath` parameter still accepts FullName
- `sql_query` MCP tool — `FullName` column still has original value for human queries

### Trade-offs

✅ Simple, no hashing overhead  
✅ Zero collision risk  
✅ Fixed 32-char key fits any backend  
✅ FullName preserved as queryable data field  
✅ Relationships reference symbols via GUID — consistent foreign key  

❌ Opaque GUID in `DependencyNode.SymbolName` (same as hash approach)  
❌ Extra round-trip: FullName lookup → GUID → relationship query  
❌ Same FullName in different files is ambiguous (first match wins)

---

All trade-offs above are **acceptable for the current design**. No further key strategy changes planned. The GUID approach is the final solution.
