# SQL Surface for InMemoryVectorStore — Spike Assessment

## Purpose

Assess the feasibility of exposing CodeMemory's indexed data (symbols, chunks, relationships) via an SQL query MCP tool, using the **SqlParserCS** library to parse SQL and translating WHERE clauses to LINQ expression trees over Memori's `InMemoryVectorStoreCollection`.

## Date

2026-05-15

## Participants

AI coding agent + human review.

---

## Background

CodeMemory has two query surfaces today:

| Surface | Backend | LLM-friendliness |
|---|---|---|
| **LiteGraph DSL** (`GraphQueryTool`) | LiteGraph property graph | Low — LLMs don't know this DSL. Agent must learn it per-repo |
| **Pre-built MCP tools** (FindRelatedCode, SemanticSearch, etc.) | IStorageService | High — but rigid. Agents can only ask pre-canned questions |

The gap: agents cannot compose arbitrary filters across indexed fields without learning a custom DSL or making multiple tool calls. SQL is universal — every LLM already knows it.

---

## Findings

### 1. SqlParserCS (v0.6.5) — Already a Dependency

`CodeMemory.AspNet.csproj` line 11:
```xml
<PackageReference Include="SqlParserCS" Version="0.6.5" />
```

But it is **completely unused** in the current codebase. The `SqlQueryTool` in AspNet is a copy-paste of `GraphQueryTool` that delegates to LiteGraph — it does not parse or execute SQL.

SqlParserCS supports:
- Full SELECT parsing: `FROM`, `WHERE`, `JOIN`, `ORDER BY`, `LIMIT`, `OFFSET`
- WHERE expression types: `BinaryOp` (Eq, NotEq, Gt, Lt, And, Or), `Like`, `InList`, `IsNull`, `IsNotNull`, `Between`, `UnaryOp(Not)`
- Dialects: Generic, SQLite, PostgreSQL, MySQL, MSSQL, 10+ more
- Round-trip SQL generation (`ToSql()`)
- Visitor pattern for AST traversal

### 2. InMemoryVectorStore (Memori) — Already Integrated

Memori's `InMemoryVectorStoreCollection<TKey, TRecord>` (extends `VectorStoreCollection<TKey, TRecord>`) has a **Memori-specific method**:

```csharp
IAsyncEnumerable<TRecord> GetAsync(
    Expression<Func<TRecord, bool>> predicate,
    int top = int.MaxValue,
    FilteredRecordRetrievalOptions<TRecord>? options = null,
    CancellationToken cancellationToken = default);
```

This iterates the internal `ConcurrentDictionary`, compiles the expression, and yields matching records. This is exactly what we need for SQL WHERE translation.

This method is NOT on the abstract `VectorStoreCollection<TKey, TRecord>` from MEVD — it is Memori-specific. The SQL surface is thus tied to the InMemoryVectorStore backend.

### 3. Storage Models — Fully Annotated

All three models (`SymbolRecord`, `ChunkRecord`, `RelationshipRecord`) have `[VectorStoreKey]`, `[VectorStoreData]`, `[VectorStoreData(IsIndexed = true)]`, and `[VectorStoreVector]` attributes.

```csharp
public sealed class SymbolRecord {
    string Id, Name, Kind, FilePath, FullName;
    int LineStart, LineEnd;
    string? Modifiers, Documentation;
}

public sealed class ChunkRecord {
    string Id, SymbolId, FilePath, Content, Language;
    int LineStart, LineEnd;
    string? MetadataJson;
    ReadOnlyMemory<float>? Embedding;
}

public sealed class RelationshipRecord {
    string Id, SourceSymbolId, TargetSymbolId, RelationshipType;
}
```

### 4. StorageService Does Not Expose the VectorStore

The `VectorStore` instance is a `private readonly` field in `StorageService`. Neither `IStorageService` nor `StorageService` expose it publicly. The SQL query service needs access to call `GetCollection<string, TRecord>(name)`.

### 5. Multi-Repo Architecture

Per-repo `VectorStore` instances are created in `Program.cs` and wrapped in `StorageService`, which is wrapped in `StorageServiceRouter` (keyed by repo name). Any SQL query service must follow the same `IRepoContextAccessor`-based routing pattern.

### 6. Embedding Generator Available

`NgramEmbeddingGenerator` is registered as `IEmbeddingGenerator<string, Embedding<float>>` in DI (line 33 of `Program.cs`). This enables the `ORDER BY Similarity DESC` vector search path.

---

## Proposed Architecture

```
LLM
  → SqlQueryAsync("SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10")
    → SqlQueryTool.SqlQueryAsync()
      → SqlQueryService.ExecuteAsync(sql)
        → SqlQueryParser.Parse(sql)         [SqlParserCS]
        → Validate: single SELECT, known table
        → Extract: FROM → table, WHERE → filterExpr, ORDER BY → sort, LIMIT → top
        → SqlExpressionBuilder.BuildFilter<T>(whereClause)
            ↓
            System.Linq.Expressions:
              Parameter → Property → Constant → Binary(Equal/AndAlso/OrElse)
            ↓
          Expression<Func<TRecord, bool>>
        → CollectionResolver.GetCollection(tableName)
            ↓
          StorageService.Store.GetCollection<string, TRecord>("symbols")
            ↓
          InMemoryVectorStoreCollection<string, TRecord>
        → collection.GetAsync(filter, top)
        → Materialize → List<Dictionary<string, object?>>
        → Return JSON result
```

### Vector Search Path

```
SQL: SELECT * FROM ChunkRecord WHERE Content LIKE '%auth%' ORDER BY Similarity DESC LIMIT 5
  → Extract query text from WHERE (token after Content LIKE)
  → NgramEmbeddingGenerator.GenerateAsync(queryText) → embedding vector
  → collection.SearchAsync(embedding, top, options with Filter)
  → Return scored results
```

---

## Supported SQL Surface

```sql
SELECT [columns] | *
  FROM SymbolRecord | ChunkRecord | RelationshipRecord
  [WHERE condition [AND | OR condition]*]
  [ORDER BY column [ASC | DESC]]
  [LIMIT n]

condition operators:  =, <>, <, >, <=, >=
                      LIKE, IN (v1, v2, ...)
                      IS NULL, IS NOT NULL
                      BETWEEN a AND b

vector search:        ORDER BY Similarity DESC
                      (triggers embedding-based search on ChunkRecord)
```

## Limitations

| Constraint | Reason |
|---|---|
| **InMemoryVectorStore only** | `GetAsync(filter)` is Memori-specific. SQLite backend already speaks SQL natively — separate concern |
| **No JOINs** | Would need client-side merge across collections. Unclear ROI for initial release |
| **No subqueries** | Would need recursive expression building. Out of scope for v1 |
| **No aggregates / GROUP BY** | Not supported by InMemoryVectorStore |
| **No DML (INSERT/UPDATE/DELETE)** | The index is owned by the indexing pipeline. SQL is read-only |
| **Full scan on filter** | InMemoryVectorStore iterates all records. Fine for repo-scale data (~10K-100K records) |

---

## What Needs Building

| Component | Lines | Description |
|---|---|---|
| `SqlExpressionBuilder.cs` | ~250 | SqlParserCS AST → `Expression<Func<TRecord, bool>>` via `System.Linq.Expressions` |
| `CollectionResolver.cs` | ~100 | Maps table names → collection + record type. Resolves per-repo `VectorStore` |
| `SqlQueryService.cs` | ~200 | Orchestration: parse → build filter → query → materialize results |
| `TableSchemaProvider.cs` | ~80 | Column metadata for MCP tool descriptions |
| `SqlQueryTool.cs` replacement | ~60 | New `SqlQueryAsync` MCP tool |
| `StorageService.cs` change | +1 line | Expose `public VectorStore Store` |
| `Program.cs` DI wiring | +3 lines | Register SQL query services |

No new NuGet dependencies. SqlParserCS already referenced.

---

## Verdict

**Feasible and worth doing.** The three core pieces (SqlParserCS for parsing, `System.Linq.Expressions` for building, `InMemoryVectorStoreCollection.GetAsync(filter)` for execution) compose cleanly. LLMs already know SQL. The implementation is self-contained in `CodeMemory.AspNet` (the experimental host) at ~700 lines of new code.
