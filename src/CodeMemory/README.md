# CodeMemory

Repository intelligence substrate — a persistent, queryable semantic memory layer for codebases, exposed via the Model Context Protocol (MCP).

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
- **SQL queries** — compose arbitrary filters across indexed fields using SQL (`SELECT`, `WHERE`, `ORDER BY`, `GROUP BY`, `HAVING`, aggregates, vector search) — parsed by SqlParserCS, executed as LINQ over InMemoryVectorStore
- **Multi-repo support** — per-repository storage isolation via `ServiceRegistry` + `IRepoContextAccessor`
- **Pluggable storage** — in-memory (`InMemoryVectorStore`, default, zero-dependency) or SQLite with vector extensions (`Microsoft.SemanticKernel.Connectors.SqliteVec`)

## Dependency Injection

```csharp
// Minimal setup — in-memory storage, offline n-gram embeddings
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>();
services.AddKeyedSingleton("default", (sp, _) => /* configure IStorageService */);
services.AddSingleton<DependencyGraphService>();
services.AddSingleton<ArchitectureService>();
services.AddSingleton<ComponentClusteringService>();
services.AddSingleton<GitHistoryService>();
services.AddSingleton<SemanticSearchService>();
services.AddSingleton<SqlQueryService>();
services.AddSingleton<CollectionRegistry>();
```

## Key Dependencies

| Package | Role |
|---|---|
| **Memori** | `NgramEmbeddingGenerator` for offline embeddings + `InMemoryVectorStore` — no API keys, no model downloads |
| **SqlParserCS** | SQL parsing (SELECT, WHERE, ORDER BY, GROUP BY, HAVING, aggregates) |
| **Microsoft.Extensions.AI.Abstractions** | `IEmbeddingGenerator`, `IChatClient` abstractions |
| **Microsoft.Extensions.VectorData.Abstractions** | Vector store abstractions |
| **ModelContextProtocol** | MCP server types and tool attributes |
| **Microsoft.CodeAnalysis.CSharp** | Roslyn-based C# parsing |
| **TreeSitter.DotNet** | Multi-language parsing (TS, JS, Java) — optional |
| **System.Numerics.Tensors** | `TensorPrimitives.Norm` for embedding normalization |

## Targets

- Repository indexing, semantic analysis, and MCP tool exposure
- AI coding agents and IDE assistants needing architecture-aware context
- Multi-repo deployments with per-repository storage isolation

## Learn More

- Full documentation and architecture: [ARCHITECTURE.md](https://github.com/khurram-uworx/CodeMemory/blob/main/ARCHITECTURE.md)
- AI agent engineering guidelines: [AGENTS.md](https://github.com/khurram-uworx/CodeMemory/blob/main/AGENTS.md)

## License

Apache-2.0
