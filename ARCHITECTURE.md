# CodeMemory Architecture

## What It Is

A repository intelligence substrate that indexes codebases into a queryable semantic memory layer, exposed entirely via MCP (Model Context Protocol).

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
│   ├── CodeMemory.AspNet/        # ASP.NET Core host + BackgroundService
│   │   ├── Configuration/        # ServiceRegistry, StorageServiceRouter, RepoContextAccessor
│   │   ├── Services/             # IndexingHostedService (BackgroundService wrapper)
│   │   ├── Program.cs            # Host entry point, DI, MCP + HTTP setup
│   │   └── appsettings.json
│   └── CodeMemory.Tests/         # NUnit tests
├── docs/
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
  ├── TreeSitter.DotNet                          (Tree-sitter parsing for TS/JS/Java)
  └── CodeMemory.Storage                         (vector store providers)
        ├── Memori                               (InMemoryVectorStore, NgramEmbeddingGenerator)
        └── Microsoft.SemanticKernel.Connectors.SqliteVec  (SQLite IVectorStore)

CodeMemory.AspNet (ASP.NET host)
  ├── CodeMemory                                 (core library)
  ├── CodeMemory.Storage                         (vector store provider)
  ├── ModelContextProtocol.AspNetCore            (MCP Streamable HTTP transport)
  └── Microsoft.Extensions.AI.Abstractions       (embedding generator DI)
```

Rules:
- `CodeMemory` is a pure library (`Microsoft.NET.Sdk`, `OutputType Library`) with zero ASP.NET dependency
- `CodeMemory.AspNet` owns all ASP.NET hosting concerns: `Program.cs`, DI registration, MCP HTTP transport, `BackgroundService` lifecycle
- `CodeMemory.Storage` holds both SQLite (`SqliteVectorStore`) and in-memory (`InMemoryVectorStore`) providers; swappable via `Storage:Provider` config key
- `IEmbeddingGenerator<string, Embedding<float>>` is provided by the `Memori` NuGet package via DI — optional in `StorageService` constructor (accepts null, no chunk storage), but always registered in the default DI wiring
- `IndexingEngine` (logic) lives in `CodeMemory.Services`; `IndexingHostedService` (BackgroundService wrapper) lives in `CodeMemory.AspNet.Services`
- MCP tools live in `CodeMemory.Mcp`; registration uses `WithToolsFromAssembly(typeof(McpTools).Assembly)` from `CodeMemory.AspNet.Program.cs`
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
  │         │    │    .cs        → RoslynCSharpParser + RoslynSymbolExtractor + ...
  │         │    │    .ts/.js    → TreeSitterParser + TreeSitterSymbolExtractor + ...
  │         │    │    .java      → TreeSitterParser + TreeSitterSymbolExtractor + ...
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
  "Storage:Provider": "inmemory" (default) or "sqlite"
  Selected in Program.cs before repo loop — constructs SqliteVectorStore or InMemoryVectorStore accordingly
```

Notes:
- Relationship extraction is syntax-only (no SemanticModel) — sufficient for MVP cross-file references
- External/BCL type references are omitted; only intra-repo relationships recorded
- Full re-index on each startup (incremental planned); non-blocking in both hosts

### Search Pipeline

```
SemanticSearchService.SearchByTextAsync(query)
  ├─ embeddingGenerator.GenerateAsync([query]) → Embedding<float>
  ├─ TensorPrimitives.Norm() → L2-normalize query vector
  ├─ storage.SearchChunksAsync(normalized, top) → vector store cosine similarity search
  ├─ optional: filter results by minimumSimilarity threshold (score ≤ 1 - minSimilarity)
  └─ return ranked List<ScoredChunk>
```

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
- **`"inmemory"`** (default) — `InMemoryVectorStore` from Memori. No persistence, data lost on restart. No external dependencies. Best for CI/testing/agent sessions.
- **`"sqlite"`** — `SqliteVectorStore` via `Microsoft.SemanticKernel.Connectors.SqliteVec`. Persistent storage at `.memorycode/sqlvec.db` per repo. Requires SQLite native binaries at runtime.

### Why Memori for Both Embeddings and In-Memory Storage

Memori was chosen as the default for two reasons that together eliminate external dependencies for a smooth out-of-box experience:

1. **`NgramEmbeddingGenerator`** — character n-gram hashing with random projection. Works fully offline: no API keys, no model downloads, no network calls. Embeddings are deterministic and L2-normalized.
2. **`InMemoryVectorStore`** — zero-configuration vector storage. No database setup, no native binaries, no connection strings. Data is ephemeral (lost on restart), which is fine for agent sessions and CI.

Together, a single `Memori` NuGet dependency provides both the embedding pipeline and the default vector store — no external infrastructure needed to run CodeMemory.

## Storage Schema

Three collections in the vector store:

| Collection | Record Type | Has Vector? | Key |
|---|---|---|---|
| `symbols` | `SymbolRecord` | No | Symbol full name |
| `chunks` | `ChunkRecord` | Yes (float32[1536], Cosine) | Chunk hash ID |
| `relationships` | `RelationshipRecord` | No | Relationship ID |

### SymbolRecord
- Id, Name, Kind (Class/Method/Property/etc.), FilePath, LineStart/End, FullName, Modifiers, Documentation

### ChunkRecord
- Id, SymbolId, FilePath, Content, Language, LineStart/End, MetadataJson, Embedding

### RelationshipRecord
- Id, SourceSymbolId, TargetSymbolId, RelationshipType (Inherits/Implements/Calls/References/TestCoverage)

Query methods on `IStorageService`:
- `GetRelationshipsBySourceAsync(sourceId)` — what this symbol references
- `GetRelationshipsByTargetAsync(targetId)` — what references this symbol
- `GetSymbolsByKindAsync(kind)` — all symbols of a kind (for architecture overview)
- `GetSymbolAsync(id)` — resolve a single symbol by full name

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

---

## MCP Tool Surface

Ten tools auto-discovered via `AddMcpServer().WithToolsFromAssembly(typeof(McpTools).Assembly)` from `CodeMemory.AspNet.Program.cs`:

| Tool | Description |
|---|---|
| `ping` | Returns `{"status":"ok","indexingCompleted":true}` or `{"status":"ok","indexingCompleted":false,"message":"..."}` — agents must back off and retry if `indexingCompleted` is false. Non-blocking indexing means this is the only way to know the index is ready. |
| `semantic_search` | Natural language code search with optional similarity threshold |
| `trace_dependency` | Symbol dependency tracing (upstream/downstream/both, configurable depth) |
| `get_architecture_overview` | Repository structure overview (components, languages, file/symbol counts) |
| `get_edit_context` | Context-aware editing scope for a symbol (source, deps, related symbols, tests) |
| `find_related_code` | Find related symbols via dependency graph (breadth-first, filterable by type) |
| `impact_analysis` | Change impact analysis (downstream deps, affected files, components, test coverage) |
| `get_component_clusters` | Logical component groupings based on inter-component coupling |
| `get_symbol_history` | Git commit history for a symbol (commits, authors, dates, recent commits) |
| `get_hotspots` | Most frequently changed files ranked by commit count |

All tools return structured JSON. Tools with external service dependencies use `GetService<T>` fallback — gracefully degrade when backing services are unavailable.

---

## Embedding & Normalization Strategy

- **Default**: `NgramEmbeddingGenerator` from the `Memori` NuGet package — character n-gram hashing (2-, 3-, 4-grams) with random projection into 1536 dimensions, L2-normalized. Works completely offline, no API keys, no model downloads.
- **Optional**: Replace by registering any `IEmbeddingGenerator<string, Embedding<float>>` (OpenAI, Ollama, etc.)
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

- Indexing: full re-index on each startup (incremental planned); non-blocking in both hosts — `ping` reports `indexingCompleted` status
- C#, TypeScript, JavaScript, Java for symbol extraction and relationships; other languages get file-level crawling only
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

## Observability

- Structured logging at every pipeline stage (indexing, search, embedding, graph, git)
- Trace IDs propagated through pipeline
- Indexing emits file counts, symbol counts, chunk counts, relationship counts, embedding stats
- Root route `GET /` returns storage provider (`storageProvider`), per-repo indexing completion (`indexingCompleted`), repo paths, and DB path (or `null` for in-memory)
