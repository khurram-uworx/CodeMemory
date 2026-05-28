# CodeMemory

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

Build architecture-aware AI agents that understand symbols, dependencies, semantics, and git history — without coupling to a specific database, LLM, or embedding provider.

## Installation

```bash
dotnet add package CodeMemory
```

## What You Get

- **Semantic code search** — natural language queries over indexed code via `IEmbeddingGenerator<string, Embedding<float>>`
- **Dependency graph** — upstream/downstream symbol relationships (calls, references, inheritance, implementation)
- **Architecture overview** — component structure, language breakdown, file and symbol counts
- **Impact analysis** — change impact with downstream dependencies, affected files, components, and test coverage
- **Component clustering** — logical groupings based on inter-component coupling density
- **Symbol history** — per-symbol git commit history with authors and timestamps
- **Hotspot detection** — most frequently changed files ranked by commit count
- **Edit context** — comprehensive context for a symbol: source code, dependency chains, related symbols, and test coverage
- **SQL queries** — compose arbitrary filters across indexed fields using SQL (`SELECT`, `WHERE`, `ORDER BY`, `GROUP BY`, `HAVING`, CTEs, derived tables, aggregates, INNER/LEFT/RIGHT/FULL OUTER/CROSS JOINs, `USING(col)`, `UNION`/`INTERSECT`/`EXCEPT`, `IN (SELECT ...)` subqueries, vector search via `ORDER BY Similarity DESC`) — parsed by SqlParserCS, executed as LINQ over InMemoryVectorStore
- **Multi-repo support** — per-repository storage isolation via `ServiceRegistry` + `IRepoContextAccessor`
- **Pluggable storage** — in-memory (`InMemoryVectorStore`, default, zero-dependency), SQLite (`Microsoft.SemanticKernel.Connectors.SqliteVec`), PostgreSQL with pgvector (`Microsoft.SemanticKernel.Connectors.PgVector`), or SQL Server (`Microsoft.SemanticKernel.Connectors.SqlServer`)
- **File watcher** — post-indexing `FileWatcherService` auto-reindexes changed/created/deleted files via `FileSystemWatcher` with debounce coalescing
- **Multi-language parsing** — Roslyn (C#) + Tree-sitter (TS/JS/Java/Python/Go/Rust/C/C++) for symbol extraction and relationships; .txt/.md indexed as text chunks

## Dependency Injection

```csharp
// Minimal setup — in-memory storage, offline n-gram embeddings
services.AddSingleton<IStorageService>(CreateInMemoryStorage(/* repoRoot */));
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();
services.AddSingleton<DependencyGraphService>();
services.AddSingleton<ArchitectureService>();
services.AddSingleton<ComponentClusteringService>();
services.AddSingleton<GitHistoryService>();
services.AddSingleton<SemanticSearchService>();
services.AddSingleton<SqlQueryService>();
services.AddSingleton<CollectionRegistry>();
```

Storage is registered via factory methods (see `CodeMemory.Storage.ServiceCollectionExtensions` or `CreateInMemoryStorage` on `IServiceProvider`). For the ASP.NET multi-repo host, storage is registered at runtime via `IServiceRegistry.Register(name, storage)` and routed through `StorageServiceRouter`.

## Key Dependencies

| Package | Role |
|---|---|
| **Memori** | `NgramEmbeddingGenerator` for offline embeddings + `InMemoryVectorStore` — no API keys, no model downloads |
| **SqlParserCS** | SQL parsing (SELECT, WHERE, ORDER BY, GROUP BY, HAVING, CTEs, aggregates) |
| **Microsoft.Extensions.AI.Abstractions** | `IEmbeddingGenerator`, `IChatClient` abstractions |
| **Microsoft.Extensions.VectorData.Abstractions** | Vector store abstractions |
| **ModelContextProtocol** | MCP server types and tool attributes |
| **Microsoft.CodeAnalysis.CSharp** | Roslyn-based C# parsing |
| **TreeSitter.DotNet** | Multi-language parsing (TS, JS, Java, Python, Go, Rust, C/C++) — optional |
| **System.Numerics.Tensors** | `TensorPrimitives.Norm` for embedding normalization |
| **Microsoft.EntityFrameworkCore** | Relational storage for symbols/relationships (AspNet hybrid path) |

## Targets

- Repository indexing, semantic analysis, and MCP tool exposure
- AI coding agents and IDE assistants needing architecture-aware context
- Multi-repo deployments with per-repository storage isolation

## Learn More

- Full documentation and architecture: [ARCHITECTURE.md](https://github.com/khurram-uworx/CodeMemory/blob/main/ARCHITECTURE.md)
- AI agent engineering guidelines: [AGENTS.md](https://github.com/khurram-uworx/CodeMemory/blob/main/AGENTS.md)

## License

MIT
