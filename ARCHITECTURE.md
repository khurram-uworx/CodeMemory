# CodeMemory Architecture — repository intelligence substrate exposed via MCP

---

## Project Structure

```
CodeMemory/
├── src/
│   ├── CodeMemory/               # Core library (no ASP.NET dependency)
│   │   ├── Indexing/
│   │   │   ├── Architecture/     # IArchitectureService, IComponentClusteringService
│   │   │   ├── Chunking/         # Semantic chunking (type-level + member-level)
│   │   │   ├── Extraction/       # Roslyn + Tree-sitter symbol & relationship extraction
│   │   │   ├── Git/              # IGitHistoryService interface
│   │   │   ├── Graph/            # IDependencyGraphService interface
│   │   │   ├── Parsing/          # Language detection + Roslyn C# + Tree-sitter parsers
│   │   │   └── Search/           # ISemanticSearchService interface
│   │   ├── Mcp/                  # MCP tool definitions + models + services
│   │   │   ├── Models/
│   │   │   └── Services/
│   │   ├── Services/
│   │   │   ├── Architecture/     # ArchitectureService, ComponentClusteringService
│   │   │   ├── Git/              # GitHistoryService
│   │   │   ├── Graph/            # DependencyGraphService
│   │   │   └── Query/            # SymbolQueryService, RelationshipQueryService, SemanticSearchService
│   │   └── Storage/              # IStorageService interface + storage model types
│   │       ├── Models/           # SymbolRecord, ChunkRecord, RelationshipRecord, ScoredChunk
│   │       └── Services/         # IStorageService interface
│   ├── CodeMemory.Storage/       # SQLite + In-memory vector store providers
│   ├── CodeMemory.Mcp/           # Standalone stdio MCP server
│   │   └── SqlQuery/             # SQL query: SqlParserCS → LINQ over InMemoryVectorStore
│   │       ├── SqlQueryService.cs
│   │       ├── SqlExpressionBuilder.cs
│   │       ├── CollectionRegistry.cs
│   │       └── TableSchemaProvider.cs
│   ├── CodeMemory.AspNet/        # ASP.NET Core host + BackgroundService
│   │   ├── Configuration/        # ServiceRegistry, StorageServiceRouter, RepoContextAccessor, StorageBootstrapper
│   │   ├── Registry/             # RepoRegistryDbContext, RepoRegistryService, RepoRegistryOptions
│   │   ├── Services/             # IndexingHostedService, MetricsService, CloneIndexService, RebuildIndexHostedService
│   │   ├── Storage/              # HybridStorageService, CodeMemoryDbContext, EF Core models + mappings
│   │   │   └── PgVector/         # PgVectorCollection, PgVectorStore, PgVectorOptions
│   │   ├── Tools/                # AspNetSqlQueryTool (relational SQL via EF Core)
│   │   ├── Pages/                # Razor Pages UI (enterprise portal: repo management, components, metrics)
│   │   ├── Program.cs            # Host entry point, DI, MCP + HTTP setup
│   │   └── appsettings.json
│   ├── CodeMemory.AspNet.Extensions/  # Optional embedding generators (ONNX, Ollama)
│   │   ├── BertOnnxEmbeddingGenerator.cs
│   │   ├── BertTokenizer.cs
│   │   ├── OnnxModelDownloader.cs
│   │   └── ServiceCollectionExtensions.cs
│   ├── CodeMemory.AppHost/       # .NET Aspire orchestration host
│   ├── CodeMemory.ServiceDefaults/  # Shared OpenTelemetry, health checks, service discovery
├── packages/
│   └── code-memory/              # @uworx/code-memory npm package
│       ├── bin/code-memory.js    # self-contained .NET binary downloader + launch
│       └── README.md
├── docs/                         # Architecture docs, SQL JOINs, TreeSitter, Vectors, releasing
├── AGENTS.md                     # Agent engineering guidelines
└── ARCHITECTURE.md               # This file
```

---

## Dependency Layering

```
CodeMemory (library — no ASP.NET dep)
  ├── Microsoft.Extensions.AI.Abstractions       (IEmbeddingGenerator, IChatClient)
  ├── System.Numerics.Tensors                    (TensorPrimitives.Norm for normalization)
  ├── ModelContextProtocol                       (MCP server types + tool attributes)
  ├── Microsoft.CodeAnalysis.CSharp              (Roslyn parsing for C#)
  ├── TreeSitter.DotNet                          (Tree-sitter parsing for TS/JS/Java/Python/Go/Rust/C/C++)
  ├── SqlParserCS                                (SQL parsing for Mcp + AspNet query paths)
  └── CodeMemory.Storage                         (vector store providers)
        ├── Memori                               (InMemoryVectorStore, NgramEmbeddingGenerator)
        ├── Microsoft.SemanticKernel.Connectors.SqliteVec  (SQLite IVectorStore)
        ├── Microsoft.SemanticKernel.Connectors.PgVector   (PostgreSQL IVectorStore)
        └── Microsoft.SemanticKernel.Connectors.SqlServer  (SQL Server IVectorStore)

CodeMemory.AspNet.Extensions (optional embedding generators)
  ├── Microsoft.ML.OnnxRuntime                   (ONNX Runtime for BERT inference)
  ├── OllamaSharp                                (Ollama API client)
  └── Microsoft.Extensions.AI.Abstractions       (IEmbeddingGenerator)

CodeMemory.ServiceDefaults (shared Aspire infrastructure)
  └── OpenTelemetry.Extensions.Hosting           (Prometheus + OTLP exporters)

CodeMemory.AspNet (ASP.NET host)
  ├── CodeMemory                                 (core library)
  ├── CodeMemory.Storage                         (vector store providers)
  ├── CodeMemory.AspNet.Extensions               (optional embedding generators)
  ├── CodeMemory.ServiceDefaults                 (OpenTelemetry, health checks)
  ├── CodeMemory.AppHost                         (Aspire orchestration)
  ├── ModelContextProtocol.AspNetCore            (MCP Streamable HTTP transport)
  ├── Microsoft.EntityFrameworkCore              (relational storage for symbols/relationships)
  └── Microsoft.Extensions.AI.Abstractions       (embedding generator DI)

CodeMemory.Mcp (stdio MCP host)
  ├── CodeMemory                                 (core library)
  ├── CodeMemory.Storage                         (vector store providers — InMemoryVectorStore only)
  └── CodeMemory.ServiceDefaults                 (OpenTelemetry, health checks)
```

Rules:
- `CodeMemory` is a pure library (`Microsoft.NET.Sdk`, `OutputType Library`) with zero ASP.NET dependency
- `CodeMemory.AspNet` owns all ASP.NET hosting concerns: `Program.cs`, DI registration, MCP HTTP transport, `BackgroundService` lifecycle
- `CodeMemory.Storage` holds all vector store providers (in-memory, SQLite, PostgreSQL, SQL Server); swappable via `Storage:Provider` config key
- `IEmbeddingGenerator<string, Embedding<float>>` is provided by the `Memori` NuGet package (default n-gram) or `CodeMemory.AspNet.Extensions` (ONNX/Ollama) via DI — optional in `StorageService` constructor (accepts null, no chunk storage), but always registered in the default DI wiring
- `IndexingEngine` (logic) lives in `CodeMemory.Services`; `IndexingHostedService` (BackgroundService wrapper) lives in `CodeMemory.AspNet.Services`
- MCP tool types live in two places: `CodeMemory.Mcp` namespace (core tools) and `CodeMemory.AspNet.Tools` (AspNet-specific). Registration in `CodeMemory.AspNet.Program.cs` uses both `WithToolsFromAssembly(typeof(McpTools).Assembly)` and `WithToolsFromAssembly(typeof(AspNetSqlQueryTool).Assembly)`
- `CodeMemory.Tests` references all three projects for integration testing

---

## Data Flow

### Indexing Pipeline (non-blocking on startup)

```
Startup
  ├─ IndexingHostedService.ExecuteAsync (BackgroundService in CodeMemory.AspNet)
  │    ├─ Initialize all storage services upfront (MCP tools queryable immediately)
  │    │
  │    └─ For each repo (sequential):
  │         ├─ set repo context
  │         ├─ IndexingEngine.RunIndexingAsync (logic in CodeMemory)
  │         │    ├─ storage.InitializeAsync()
  │         │    ├─ crawler.WalkAsync() — walks repo, respects .gitignore
  │         │    │
  │         │    └─ for each supported file (routed by language):
  │         │         ├─ ILanguageParser.ParseAsync() → ParseResult (Roslyn or Tree-sitter)
  │         │         ├─ ISymbolExtractor.Extract() → List<Symbol>
  │         │         ├─ IRelationshipExtractor.ExtractRelationships() → List<Relationship>
  │         │         ├─ SemanticChunker.ChunkAll() → List<DocumentChunk>
  │         │         └─ accumulate symbols + relationships + chunks
  │         │    │
  │         │    │  Language routing:
  │         │    │    .cs             → RoslynCSharpParser + RoslynSymbolExtractor + ...
  │         │    │    .ts/.tsx/.js    → TreeSitterParser + TreeSitterSymbolExtractor + ...
  │         │    │    .java           → TreeSitterParser + TreeSitterSymbolExtractor + ...
  │         │    │    .py             → TreeSitterParser + TreeSitterSymbolExtractor + ...
  │         │    │    .go             → TreeSitterParser + TreeSitterSymbolExtractor + ...
  │         │    │    .rs             → TreeSitterParser + TreeSitterSymbolExtractor + ...
  │         │    │    .c/.h/.cpp/...  → TreeSitterParser + TreeSitterSymbolExtractor + ...
  │         │    │    .txt/.md        → ILanguageParser (TextReader only) — no extraction
  │         │    │
  │         │    ├─ storage.StoreSymbolsAsync(allSymbols)
  │         │    ├─ storage.StoreRelationshipsAsync(allRelationships)
  │         │    │
  │         │    └─ if IEmbeddingGenerator registered:
  │         │         ├─ embeddingGenerator.GenerateAsync(chunkContents) → Embedding<float>[]
  │         │         ├─ TensorPrimitives.Norm() per vector → L2-normalize
  │         │         └─ storage.StoreChunksAsync(chunks with normalized embeddings)
  │         │
  │         └─ IndexingState.MarkCompleted(name) — ping tool reports indexingCompleted:true
  │
  └─ CodeMemory.Mcp (Task.Run — non-blocking before host.RunAsync)
       └─ same IndexingEngine.RunIndexingAsync in background thread
       └─ IndexingState.MarkCompleted(repoRoot) on completion
       └─ MCP server loop starts immediately; ping reports indexing status

Storage provider selection:
  "Storage:Provider": "inmemory" (default), "sqlite", "pgvector", or "sqlserver"
  Selected in Program.cs before repo loop — constructs the appropriate VectorStore + EF Core provider combination
```

Notes:
- Relationship extraction is syntax-only (no SemanticModel) — sufficient for MVP cross-file references
- External/BCL type references are omitted; only intra-repo relationships recorded
- Full re-index on each startup (incremental planned); non-blocking in both hosts
- SQL queries: InMemoryVectorStore backend (`Storage:Provider: "inmemory"`) uses SqlParserCS via `SqlQueryService`; relational backends (`sqlite`/`pgvector`/`sqlserver`) use `AspNetSqlQueryTool` forwarding to EF Core for symbols+relationships only (chunks via `semantic_search`)

### Search Pipeline

```
SemanticSearchService.SearchByTextAsync(query)
  ├─ embeddingGenerator.GenerateAsync([query]) → Embedding<float>
  ├─ TensorPrimitives.Norm() → L2-normalize query vector
  ├─ storage.SearchChunksAsync(normalized, top) → vector store cosine similarity search
  ├─ optional: filter results by minimumSimilarity threshold (score ≤ 1 - minSimilarity)
  └─ return ranked List<ScoredChunk>
```

### SQL Query Engine

```
SqlQueryTool.SqlQueryAsync("SELECT Name FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10")
  └─ SqlQueryService.ExecuteAsync(store, sql)
       ├─ SqlParserCS.Parse(sql) → AST
       ├─ detect: single SELECT, known table(s), JOINs, UNION, subqueries
       ├─ UNION/INTERSECT/EXCEPT path (SetExpression.SetOperation):
       │    └─ executeSetOperationAsync()
       │         ├─ Materialize CTEs (if present)
       │         ├─ evaluateSetExpressionForUnionAsync(left) + (right)
       │         └─ applySetOperation() → concat/intersect/except with dedup
       │
       ├─ Multi-table path (detectMultiTable):
       │    └─ executeJoinQueryAsync(from, cteResults, whereExpr)
       │         ├─ evaluateGroupAsync() per TableWithJoins:
       │         │    ├─ evaluate relation (Table/Derived/NestedJoin)
       │         │    ├─ for each JOIN step: extractJoinInfo → (JoinType, ON)
       │         │    ├─ generateUsingCondition() for USING(col)
       │         │    └─ mergeWithJoinType(INNER/LEFT/RIGHT/FULL/CROSS)
       │         ├─ cross-join groups (comma-separated items)
       │         └─ materializeSubqueriesAsync() → filterCteRows()
       │
       ├─ Single-table path:
       │    ├─ Resolve table source (Table/Derived/CTE)
       │    ├─ Fetch data:
       │    │    ├─ CTE/derived: filterCteRows(cteResults[name], whereExpr)
       │    │    ├─ Standard: collection.GetAsync(filter, top) → IAsyncEnumerable<TRecord>
       │    │    │    └─ SqlExpressionBuilder.BuildFilter<TRecord>(where) → Expression
       │    │    └─ Vector search (ORDER BY Similarity DESC):
       │    │         ├─ extract text from Content LIKE '%pattern%'
       │    │         ├─ NgramEmbeddingGenerator → embedding vector
       │    │         ├─ collection.SearchAsync(vector, top, options)
       │    │         └─ return rows with __score (0-1)
       │    └─ materializeSubqueriesAsync() in WHERE for all paths
       │
       ├─ Client-side post-processing (shared):
       │    ├─ GROUP BY / aggregates (COUNT/SUM/AVG/MIN/MAX)
       │    ├─ DISTINCT dedup
       │    ├─ HAVING filter
       │    ├─ ORDER BY (column names, aliases, numeric positions, multiple expressions)
       │    ├─ LIMIT truncation
       │    └─ column projection (explicit SELECT list or wildcard)
       │
       └─ return SqlQueryResult(success, rowCount, executionTimeMs, columns, rows)

Storage schema metadata via TableSchemaProvider:
  └─ GetColumns<T>() → ColumnInfo[] (name, type, nullable, key, vector flag)
  └─ GetJoinKeys() → JoinKeyInfo[] (7 known foreign-key pairs)
  └─ DescribeAll() → formatted text including join keys
```

### File Watcher (Post-Indexing Auto-Reindex)

After initial indexing completes, `FileWatcherService` monitors the repo directory via `FileSystemWatcher`:

```
Indexing completes
  └─ FileWatcherService.StartAsync()
       ├─ FileSystemWatcher on repo root (NotifyFilter: LastWrite, FileName)
       ├─ debounceTimer (1s) — coalesces rapid file change events
       └─ on debounce tick:
            ├─ batch-reindex changed files:
            │    ├─ for each modified file: delete old symbol+chunk, parse, extract, store
            │    └─ for each created file: parse, extract, store symbols + chunks
            ├─ for each deleted file: remove symbols + chunks from storage
            └─ batch complete → next debounce cycle
```

The `ping` tool includes `"fileWatcherActive":true` once the watcher is running. Only active in the stdio Mcp host; AspNet host uses full re-index on startup plus optional cron-based scheduled rebuild (see below).

### Scheduled Re-Indexing (Asp.Net Host)

`RebuildIndexHostedService` provides cron-based periodic full rebuilds in the AspNet host, configured via `RebuildIndex:Cron` in `appsettings.json`:

```
RebuildIndexHostedService.ExecuteAsync
  └─ wait for next cron occurrence (via Cronos)
       └─ rebuildAsync()
            ├─ SemaphoreSlim gate (prevents overlapping rebuilds)
            ├─ for each registered repo:
            │    ├─ git pull --ff-only (if GitUrl set)
            │    ├─ storage.ClearAllAsync()
            │    ├─ IndexingState.MarkIncomplete(name)
            │    ├─ IndexingEngine.RunIndexingAsync(path) with retry
            │    └─ IndexingState.MarkCompleted(name)
            └─ wait for next cron occurrence
```

If no `RebuildIndex:Cron` is configured, the service skips silently. Retry logic (count + exponential backoff) is shared with the startup `IndexingHostedService` via `IndexingOptions`.

### Architecture Intelligence Queries

```
get_architecture_overview
  └─ ArchitectureService.GetOverviewAsync()
       ├─ storage.GetSymbolsByKindAsync(kind) per known kind
       ├─ group by top-level directory → ComponentInfo[]
       ├─ classify by file extension → language breakdown
       └─ return ArchitectureOverview(components, languages, totals)

trace_dependency / find_related_code / impact_analysis
  └─ DependencyGraphService
       ├─ TraceAsync(symbol, direction, depth) — BFS with visited-set, depth ≤ 3
       ├─ FindRelatedAsync(symbol, type) — flat filtered query
       └─ FindTestCoverageAsync(symbol) — convention (*Test.cs) or stored TestCoverage

get_component_clusters
  └─ ComponentClusteringService.GetClustersAsync(threshold)
       ├─ load symbols + build symbol→component map
       ├─ per-symbol: GetRelationshipsBySourceAsync → component coupling matrix
       └─ BFS on thresholded adjacency graph → ComponentCluster[]

get_symbol_history / get_hotspots
  └─ GitHistoryService
       ├─ GetSymbolHistoryAsync(symbol) — git log --follow on symbol's file
       ├─ GetHotspotsAsync(top) — git log --diff-filter=AM → rank by commit count
       └─ in-memory cache with 5-min TTL; shell git commands
```

---

## Storage Providers

Configurable via `Storage:Provider` in appsettings.json:

| Provider | Vector Store | Relational Store | Symbol/Relationship Query | Chunk Query |
|---|---|---|---|---|
| `"inmemory"` | `InMemoryVectorStore` (Memori) | None — all in vector store collections | LINQ over `InMemoryVectorStore` | LINQ over `InMemoryVectorStore` |
| `"sqlite"` | `SqliteVectorStore` (SK) | EF Core SQLite (`CodeMemoryDbContext`) | SQL via EF Core (`AspNetSqlQueryTool`) | Vector store (`semantic_search`) |
| `"pgvector"` | `PostgresVectorStore` (SK) | EF Core Npgsql | SQL via EF Core (`AspNetSqlQueryTool`) | Vector store (`semantic_search`) |
| `"sqlserver"` | `SqlServerVectorStore` (SK) | EF Core SQL Server | SQL via EF Core (`AspNetSqlQueryTool`) | Vector store (`semantic_search`) |

- **`"inmemory"`** (default) — `InMemoryVectorStore` from Memori. No persistence, data lost on restart. No external dependencies. Best for CI/testing/agent sessions.
- **`"sqlite"`** — `SqliteVectorStore` via `Microsoft.SemanticKernel.Connectors.SqliteVec`. Persistent storage at `.codememory/sqlvec.db` per repo. Uses `HybridStorageService` — symbols/relationships in EF Core SQLite tables, chunks in vector store.
- **`"pgvector"`** — `PostgresVectorStore` + EF Core PostgreSQL (Npgsql). Per-repo schema isolation. Requires PostgreSQL + pgvector extension.
- **`"sqlserver"`** — `SqlServerVectorStore` + EF Core SQL Server. Per-repo schema isolation. Requires SQL Server.

Relational providers (`sqlite`/`pgvector`/`sqlserver`) use `HybridStorageService` — symbols and relationships in EF Core tables for efficient relational SQL, chunks in the vector store for similarity search. The `Mcp` (stdio) host always uses `InMemoryVectorStore` only.

> **VectorData version pin:** `Microsoft.Extensions.VectorData.Abstractions` is pinned at `10.1.0` — the highest version compatible with `Microsoft.SemanticKernel.Connectors.SqliteVec 1.74.0-preview`. Newer `10.x` versions add members to `VectorSearchOptions<T>` that cause `MissingMethodException` in the SK connector. Bump only when the SK connector's minimum dependency moves past `10.1.0`.

### Why Memori for Both Embeddings and In-Memory Storage

Memori was chosen as the default for two reasons that together eliminate external dependencies for a smooth out-of-box experience:

1. **`NgramEmbeddingGenerator`** — character n-gram hashing with random projection. Works fully offline: no API keys, no model downloads, no network calls. Embeddings are deterministic and L2-normalized.
2. **`InMemoryVectorStore`** — zero-configuration vector storage. No database setup, no native binaries, no connection strings. Data is ephemeral (lost on restart), which is fine for agent sessions and CI.

Together, a single `Memori` NuGet dependency provides both the embedding pipeline and the default vector store — no external infrastructure needed to run CodeMemory.

## Storage Schema

### Mcp (STDIO) path — InMemoryVectorStore

Three collections in the vector store:

| Collection | Record Type | Has Vector? | Key |
|---|---|---|---|
| `symbols` | `SymbolRecord` | No | GUID |
| `chunks` | `ChunkRecord` | Yes (float32[1536], Cosine) | SHA256 hash |
| `relationships` | `RelationshipRecord` | No | `sourceId->targetId:type` composite |

### AspNet (HTTP) path — HybridStorageService

Symbols and relationships stored in relational tables via EF Core (`symbols` / `relationships` tables). Chunks stored in the VectorStore (same schema as Mcp path).

### SymbolRecord
- Id (GUID), Name, Kind (Class/Method/Property/etc.), FilePath, LineStart/End, FullName, Modifiers, Documentation

### ChunkRecord
- Id (SHA256 hash), SymbolId, FilePath, Content, Language, LineStart/End, MetadataJson, Embedding

### RelationshipRecord
- Id (`sourceId->targetId:type`), SourceSymbolId, TargetSymbolId, RelationshipType (Inherits/Implements/Calls/References/TestCoverage)

Query methods on `IStorageService`:
- `GetRelationshipsBySourceAsync(sourceId)` — what this symbol references
- `GetRelationshipsByTargetAsync(targetId)` — what references this symbol
- `GetSymbolsByKindAsync(kind)` — all symbols of a kind (for architecture overview)
- `GetSymbolByFullNameAsync(fullName)` — resolve a single symbol by its FullName (returns GUID Id)
- `GetSymbolAsync(id)` — resolve a single symbol by GUID Id

---

## Key Abstractions

| Abstraction | Source | Purpose |
|---|---|---|
| `IEmbeddingGenerator<string, Embedding<float>>` | `Microsoft.Extensions.AI.Abstractions` | Generate text embeddings |
| `VectorStore` (via `IVectorStore`) | `Microsoft.Extensions.VectorData` | Abstract vector storage |
| `IStorageService` | `CodeMemory.Storage` | CRUD over symbols, chunks, relationships |
| `ISemanticSearchService` | `CodeMemory` | Text/vector-based semantic search |
| `ILanguageParser` | `CodeMemory` | Parse source files to parse results (Roslyn or Tree-sitter) |
| `ISymbolExtractor` | `CodeMemory` | Extract symbols from parse results |
| `IRelationshipExtractor` | `CodeMemory` | Extract relationships (inherits, calls, references) from symbols |
| `IDependencyGraphService` | `CodeMemory.Indexing.Graph` | Dependency tracing, related symbols, test coverage |
| `IArchitectureService` | `CodeMemory.Indexing.Architecture` | Component grouping, language breakdown, file/symbol counts |
| `IComponentClusteringService` | `CodeMemory.Indexing.Architecture` | Threshold-based component coupling clustering |
| `IGitHistoryService` | `CodeMemory.Indexing.Git` | Symbol git history, hotspot analysis |
| `SqlQueryService` | `CodeMemory.Mcp.SqlQuery` | SQL parsing + execution: SqlParserCS → LINQ → InMemoryVectorStore |
| `CollectionRegistry` | `CodeMemory.Mcp.SqlQuery` | Table name → VectorStore collection mapping (SymbolRecord, ChunkRecord, RelationshipRecord) |
| `SqlExpressionBuilder` | `CodeMemory.Mcp.SqlQuery` | SQL WHERE AST → `Expression<Func<TRecord, bool>>` via `System.Linq.Expressions` |
| `TableSchemaProvider` | `CodeMemory.Mcp.SqlQuery` | Reflective column metadata for MCP tool descriptions |

---

## Embedding Providers

Configurable via `Embedding:Provider` in `appsettings.json`. All backends implement `IEmbeddingGenerator<string, Embedding<float>>` and are swappable via DI:

| Provider | Key | Description | Dependencies |
|---|---|---|---|
| N-gram (default) | `"ngram"` | Character n-gram hashing with random projection into 1536 dims. Deterministic, offline, no API keys. | `Memori` NuGet |
| ONNX | `"onnx"` | BERT-based embedding via ONNX Runtime (`bge-micro-v2` model). Requires `download-models.ps1`. | `CodeMemory.AspNet.Extensions`, `Microsoft.ML.OnnxRuntime` |
| Ollama | `"ollama"` | Ollama server embedding API (default model `all-minilm`). Requires Ollama server. | `CodeMemory.AspNet.Extensions`, `OllamaSharp` |

### Embedding & Normalization Strategy

- **Default**: `NgramEmbeddingGenerator` from the `Memori` NuGet package — character n-gram hashing (2-, 3-, 4-grams) with random projection into 1536 dimensions, L2-normalized. Works completely offline, no API keys, no model downloads.
- **Optional**: Replace by registering any `IEmbeddingGenerator<string, Embedding<float>>` (OpenAI via SK connector, Ollama, etc.)
- Vectors are L2-normalized after generation using `TensorPrimitives` before storage
- Query vectors are also normalized before search for consistent cosine distance computation
- Normalization is a safety net — works correctly whether or not the model returns unit vectors
- Embeddings cached per indexing run (not recomputed unnecessarily)

---

## Semantic Chunking

- AST-based (Roslyn for C#, Tree-sitter for TS/JS/Java), not fixed-token-window
- Two chunk types per file:
  - **Type chunks**: class/interface/struct/enum/record — includes file context (usings/namespace for C#, imports/exports for TS/JS/Java)
  - **Member chunks**: method/property/field/event — includes parent type reference
- Chunks identified by SHA256 hash of (symbolId + content + filePath)
- Deterministic: same input → same chunk IDs

---

## Architecture Intelligence Services

### Dependency Graph (`DependencyGraphService`)
- BFS traversal with depth limit (capped at 3) and visited-set for cycle safety
- Three query modes: `TraceAsync` (chain), `FindRelatedAsync` (flat), `FindTestCoverageAsync` (convention)
- Used by: `trace_dependency`, `find_related_code`, `impact_analysis`, `get_edit_context`

### Architecture Overview (`ArchitectureService`)
- Per-kind symbol queries (avoids loading all symbols at once)
- Groups by top-level directory, detects language from file extension
- Used by: `get_architecture_overview`, `impact_analysis`

### Component Clustering (`ComponentClusteringService`)
- Builds component-to-component dependency matrix from stored relationships
- Coupling = inter-component edges / total edges; threshold-based adjacency
- BFS on thresholded graph forms clusters with cohesion scores
- Used by: `get_component_clusters`

### Git History (`GitHistoryService`)
- Shell `git` commands with `--no-pager` (no libgit2sharp dependency)
- In-memory `ConcurrentDictionary` cache with 5-minute TTL and periodic cleanup
- Graceful degradation in non-git repos or when git is unavailable
- Used by: `get_symbol_history`, `get_hotspots`

## Enterprise Portal (AspNet Razor Pages)

The AspNet host includes a web UI for managing repositories and browsing engineering metrics:

### Features
- **Add repos** — specify a local path or GitHub URL (auto-cloned to `CloneBasePath`)
- **Status dashboard** — per-repo indexing status, clone progress, error messages, live SSE updates
- **Component browser** — view discovered build-file components (MsBuild, Maven, Node, Cargo, Go, etc.), classify their kind and type, soft-delete unwanted entries
- **Code metrics** — symbol-kind distribution, method complexity (lines per method, size histogram), coupling analysis (most-coupled symbols, most-important symbols, relationship-type distribution), top files by symbol count
- **Registry-backed** — `RepoRegistryDbContext` (SQLite/SQL Server/PostgreSQL) persists repo configurations across restarts

### Architecture
```
Razor Pages UI (GET/POST /Repos/Add, GET /)
  └─ RepoRegistryService
       ├─ ListAsync / GetAsync — read from RepoRegistryDbContext (EF Core)
       └─ CreateRepoAsync — insert new repo config
            └─ CloneIndexService
                 ├─ git clone (if URL) into CloneBasePath/{repoName}
                 ├─ create per-repo storage (IStorageService)
                 ├─ run IndexingEngine → store symbols/chunks/relationships
                 └─ register in ServiceRegistry for MCP endpoint
```

REST endpoints (`GET /api/repos`, `GET /api/repos/{name}/status`, `GET /api/repos/stream`) expose registry data for external monitoring and live SSE streaming.

### Metrics Page (`/Repos/{name}/Metrics`)

The metrics dashboard queries `IStorageService` through `MetricsService` to render:

- **Overview cards** — total symbols, files, classes, methods, interfaces, properties, fields, relationships
- **Symbol-kind distribution** — bar chart of counts per symbol kind
- **Method complexity** — average lines per method, longest methods table, size histogram (bucketed by line count)
- **Coupling analysis** — most-coupled symbols (highest outgoing relationship count), most-important symbols (highest incoming), relationship-type distribution
- **Top files by symbol count** — ranked table of files with the most symbols

### Component Management (`/Repos/{name}/Components`)

Displays all build-file-detected components for a repo. Each component row shows:
- Build file path, component name, configurable component kind (MsBuild/Maven/Node/Cargo/Go/Gradle/Python/CMake/Haskell/Folder) and type (Component/Test/Tool/Documentation/Example/Other)
- Editable via inline dropdowns; soft-delete via POST handler (hidden components do not reappear on re-index)

## Multi-Repo Architecture

### Design

Multi-repo support uses **`StorageServiceRouter` + `IRepoContextAccessor` (AsyncLocal) + per-repo MCP endpoints** — no keyed DI, no middleware, no `RequestServices` swap.

**How it works:** Each repo gets its own MCP endpoint via `MapMcp("/api/mcp/{repoName}")`. The MCP SDK's `ConfigureSessionOptions` callback extracts the repo name from the URL path and sets `IRepoContextAccessor.CurrentRepoName`. All services remain non-keyed — they depend on `IStorageService` which delegates to the correct per-repo storage via `StorageServiceRouter`.

### Data flow (multi-repo request)

```
HTTP POST /api/mcp/repo1
  └─ MCP handler (MapMcp("/api/mcp/repo1"))
       └─ ConfigureSessionOptions callback
            ├─ extracts "repo1" from URL path segments
            └─ sets IRepoContextAccessor.CurrentRepoName = "repo1"
                 └─ PerSessionExecutionContext preserves AsyncLocal for handler
                      └─ tool resolves IStorageService → StorageServiceRouter
                            └─ GetStorage() → registry.GetStorage("repo1") → repo1's storage
```

### Key components

| Component | Location | Purpose |
|---|---|---|
| `ServiceRegistry` / `IServiceRegistry` | `CodeMemory.AspNet/Configuration/` | Thread-safe `ConcurrentDictionary` of per-repo `IStorageService` instances; generalized from the old `IStorageServiceRegistry` |
| `StorageServiceRouter` | `CodeMemory.AspNet/Configuration/` | Delegates all 15 `IStorageService` methods via `GetStorage()` using ambient repo context |
| `IRepoContextAccessor` / `RepoContextAccessor` | `CodeMemory.AspNet/Configuration/` | `AsyncLocal<string?>` — singleton-safe, no scoped DI, flows with ExecutionContext |
| `ConfigureSessionOptions` | `CodeMemory.AspNet/Program.cs` | MCP SDK callback that extracts repo name from URL path |

### Constraints

- `IStorageService` is the **only** per-repo concern. All other services stay non-keyed (singleton).
- `Stateless = true` (Streamable HTTP) — no session affinity needed, each request is self-contained.
- `PerSessionExecutionContext = true` preserves `AsyncLocal` (and `IRepoContextAccessor`) across the handler chain.
- No middleware, no path rewriting, no `RequestServices` swap — clean ASP.NET pipeline.
- If no repos are configured, no MCP endpoints are registered at all.

### Indexing

`IndexingHostedService` sets `IRepoContextAccessor.CurrentRepoName` before each indexing iteration so `StorageServiceRouter` delegates to the correct DB. All storage services are initialized upfront before sequential indexing begins:

```csharp
// Initialize all storage services upfront
foreach (var (name, _) in repositories)
{
    var storage = registry.GetStorage(name);
    await storage.InitializeAsync(stoppingToken);
}

// Then index each repo sequentially
foreach (var (name, path) in repositories)
{
    repoContext.CurrentRepoName = name;
    repoContext.CurrentRepoRoot = repoPath;
    using var scope = serviceProvider.CreateScope();
    var engine = scope.ServiceProvider.GetRequiredService<IndexingEngine>();
    await engine.RunIndexingAsync(repoPath, stoppingToken);
}
```

---

## Current Constraints & Limitations

- Indexing: full re-index on each startup (incremental planned); non-blocking in both hosts — `ping` reports `indexingCompleted` status. Mcp host supports file-watcher-based incremental reindexing post-startup.
- C#, TypeScript/TSX, JavaScript/JSX, Java, Python, Go, Rust, C/C++ for full symbol extraction and relationships; text and markdown files indexed as ChunkRecord with Language='Text' (no symbol extraction)
- Embedding dimension is auto-detected from the registered `IEmbeddingGenerator` metadata (default 1536, provided by Memori's `NgramEmbeddingGenerator`)
- Relationship extraction is syntax-only — overloaded method references may be imprecise
- Git analysis uses shell commands (acceptable per design, but slower than native library)
- In-memory storage: all data lost on restart; intended for CI/agent sessions, not production persistence

---

## Performance

- Batched embedding generation (all chunks at once)
- Batched vector store writes
- SIMD-accelerated normalization via `TensorPrimitives`
- Dependency graph uses filtered queries per hop (not full collection scans)

## Metrics & Observability

### OpenTelemetry Instrumentation (AspNet + ServiceDefaults)

Both hosts use OpenTelemetry via `CodeMemory.ServiceDefaults`:

```
AddServiceDefaults()
  ├─ ConfigureOpenTelemetry()
  │    ├─ WithMetrics()
  │    │    ├─ AddAspNetCoreInstrumentation()
  │    │    ├─ AddHttpClientInstrumentation()
  │    │    ├─ AddRuntimeInstrumentation()
  │    │    ├─ AddMeter("CodeMemory")       ← custom instruments
  │    │    └─ AddPrometheusExporter()      ← /metrics endpoint on port 8080
  │    └─ WithTracing()
  │         ├─ AddAspNetCoreInstrumentation()
  │         └─ AddHttpClientInstrumentation()
  └─ if OTEL_EXPORTER_OTLP_ENDPOINT set:
       └─ UseOtlpExporter()                ← Aspire Dashboard
```

### Custom Instruments

Defined on the `CodeMemory` meter (`src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs`):

| Instrument | Type | Description |
|---|---|---|
| `codememory.indexing.duration` | Histogram (ms) | Full indexing pass per repo |
| `codememory.indexing.files_count` | Counter | Files indexed per repo |
| `codememory.indexing.symbols_count` | Counter | Symbols stored per repo |
| `codememory.git.clone.duration` | Histogram (ms) | Git clone operations |
| `codememory.search.query_duration` | Histogram (ms) | Semantic search queries |
| `codememory.sql.query_duration` | Histogram (ms) | Custom SQL queries |
| `codememory.tools.invocations` | Counter | MCP tool invocations (tagged by tool, host) |

See [`METRICS.md`](docs/METRICS.md) for full instrument definitions, Prometheus scrape config, and Grafana dashboard reference.

### Observability

- Structured logging at every pipeline stage (indexing, search, embedding, graph, git)
- Trace IDs propagated through pipeline
- Indexing emits file counts, symbol counts, chunk counts, relationship counts, embedding stats
- **Mcp host** uses `CodeMemoryFileLogger` (writes `Log.*.txt` to `.codememory/` folder) + `StdErrorLogger<T>` (stderr mirror, informational-only on stdout)
- **AspNet host** uses standard `ILogger<T>` via ASP.NET Core logging infrastructure
- Root route `GET /` returns storage provider (`storageProvider`), per-repo indexing completion (`indexingCompleted`), repo paths, and DB path (or `null` for in-memory)
- Health check endpoints: `GET /health` (all checks), `GET /alive` (liveness)
