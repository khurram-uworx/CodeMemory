# CodeMemory Architecture

## What It Is

A repository intelligence substrate that indexes codebases into a queryable semantic memory layer, exposed entirely via MCP (Model Context Protocol).

---

## Project Structure

```
CodeMemory/
├── src/
│   ├── CodeMemory/               # ASP.NET Core host + Background Service
│   │   ├── Indexing/
│   │   │   ├── Architecture/     # IArchitectureService, IComponentClusteringService
│   │   │   ├── Chunking/         # Semantic chunking (type-level + member-level)
│   │   │   ├── Extraction/       # Roslyn symbol + relationship extraction
│   │   │   ├── Git/              # IGitHistoryService interface
│   │   │   ├── Graph/            # IDependencyGraphService interface
│   │   │   ├── Parsing/          # Language detection + Roslyn C# parser
│   │   │   └── Search/           # ISemanticSearchService interface
│   │   ├── Mcp/                  # MCP tool definitions + models + services
│   │   │   ├── Models/
│   │   │   └── Services/
│   │   └── Services/
│   │       ├── Architecture/     # ArchitectureService, ComponentClusteringService
│   │       ├── Embedding/        # NgramEmbeddingGenerator
│   │       ├── Git/              # GitHistoryService
│   │       ├── Graph/            # DependencyGraphService
│   │       └── Query/            # SymbolQueryService, RelationshipQueryService, SemanticSearchService
│   ├── CodeMemory.Storage/       # SQLite vector store provider
│   │   ├── Models/               # VectorStore record types
│   │   └── Services/             # IStorageService + implementation
│   └── CodeMemory.Tests/         # NUnit tests
├── docs/
├── .index/                       # Runtime SQLite database (auto-created)
├── AGENTS.md                     # Agent engineering guidelines
└── ARCHITECTURE.md               # This file
```

---

## Dependency Layering

```
CodeMemory (host)
  ├── Microsoft.Extensions.AI.Abstractions    (IEmbeddingGenerator, IChatClient)
  ├── System.Numerics.Tensors                 (TensorPrimitives.Norm for normalization)
  ├── ModelContextProtocol.AspNetCore         (MCP server hosting)
  ├── Microsoft.CodeAnalysis.CSharp           (Roslyn parsing)
  └── CodeMemory.Storage                      (vector store provider)
        └── Microsoft.SemanticKernel.Connectors.SqliteVec  (implements IVectorStore)
```

Rules:
- `CodeMemory` references only abstractions (`Microsoft.Extensions.AI.Abstractions`), never concrete AI SDKs
- `CodeMemory.Storage` is the sole project with a concrete vector store driver; swappable (pgvector, Qdrant, etc.)
- `IEmbeddingGenerator<string, Embedding<float>>` is user-provided via DI — optional; indexing proceeds without embeddings if absent
- `CodeMemory.Tests` references both projects for integration testing

---

## Data Flow

### Indexing Pipeline (eager on startup)

```
Startup
  └─ IndexingService.ExecuteAsync (BackgroundService)
       ├─ storage.InitializeAsync()
       ├─ crawler.WalkAsync() — walks repo, respects .gitignore
       │
       └─ for each .cs file:
            ├─ RoslynCSharpParser.ParseAsync() → SyntaxTree
            ├─ RoslynSymbolExtractor.Extract() → List<Symbol>
            ├─ RoslynRelationshipExtractor.ExtractRelationships() → List<Relationship>
            ├─ SemanticChunker.ChunkAll() → List<DocumentChunk>
            └─ accumulate symbols + relationships + chunks
       │
       ├─ storage.StoreSymbolsAsync(allSymbols)
       ├─ storage.StoreRelationshipsAsync(allRelationships)
       │
       └─ if IEmbeddingGenerator registered:
            ├─ embeddingGenerator.GenerateAsync(chunkContents) → List<Embedding<float>>
            ├─ TensorPrimitives.Norm() per vector → L2-normalize
            └─ storage.StoreChunksAsync(chunks with normalized embeddings)
```

Notes:
- Relationship extraction is syntax-only (no SemanticModel) — sufficient for MVP cross-file references
- External/BCL type references are omitted; only intra-repo relationships recorded
- Full re-index on each startup (incremental planned)

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

## Storage Schema

Three collections in the SQLite vector store (`.index/codememory.db`):

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
| `ILanguageParser` | `CodeMemory` | Parse source files to syntax trees |
| `IDependencyGraphService` | `CodeMemory.Indexing.Graph` | Dependency tracing, related symbols, test coverage |
| `IArchitectureService` | `CodeMemory.Indexing.Architecture` | Component grouping, language breakdown, file/symbol counts |
| `IComponentClusteringService` | `CodeMemory.Indexing.Architecture` | Threshold-based component coupling clustering |
| `IGitHistoryService` | `CodeMemory.Indexing.Git` | Symbol git history, hotspot analysis |

---

## MCP Tool Surface

Ten tools auto-discovered via `AddMcpServer().WithToolsFromAssembly()`:

| Tool | Description |
|---|---|
| `ping` | Health check |
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

- **Default**: `NgramEmbeddingGenerator` — zero-dependency built-in using character n-gram hashing (2-, 3-, 4-grams) with random projection into 1536 dimensions, L2-normalized. Works completely offline, no API keys, no model downloads.
- **Optional**: Replace by registering any `IEmbeddingGenerator<string, Embedding<float>>` (OpenAI, Ollama, etc.)
- Vectors are L2-normalized after generation using `TensorPrimitives` before storage
- Query vectors are also normalized before search for consistent cosine distance computation
- Normalization is a safety net — works correctly whether or not the model returns unit vectors
- Embeddings cached per indexing run (not recomputed unnecessarily)

---

## Semantic Chunking

- AST-based (Roslyn), not fixed-token-window
- Two chunk types per .cs file:
  - **Type chunks**: class/interface/struct/enum/record — includes file context (usings, namespace)
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

---

## Current Constraints & Limitations

- Indexing: full re-index on each startup (incremental planned)
- C# only for symbol extraction; other languages get file-level crawling only
- Embedding dimension fixed at 1536
- Relationship extraction is syntax-only — overloaded method references may be imprecise
- Git analysis uses shell commands (acceptable per design, but slower than native library)

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
