# Vectors — Microsoft VectorData Guidance

Findings from Microsoft Learn research (May 2026) about `Microsoft.Extensions.VectorData`, embedding generation, and data ingestion patterns relevant to CodeMemory's architecture.

---

## Microsoft.Extensions.VectorData Abstractions

The `Microsoft.Extensions.VectorData.Abstractions` NuGet package provides a unified layer for interacting with vector stores in .NET. Key capabilities:

- **Seamless .NET type mapping** — Map .NET types to database schema, similar to an ORM
- **Unified data model** — Define data model once with attributes; use across any supported vector store
- **CRUD operations** — Create, read, update, delete records
- **Vector and hybrid search** — Semantic similarity search, or combine text + vector search
- **Embedding generation management** — Configure an embedding generator once; the library handles generation transparently
- **Collection management** — Create, list, delete collections (tables/indices)

### Key types

| Type | Role |
|---|---|
| `VectorStore` (abstract class, was `IVectorStore`) | Entry point, holds collections |
| `VectorStoreCollection<TKey, TRecord>` (abstract class, was `IVectorStoreRecordCollection`) | Typed collection access |
| `VectorStoreKeyAttribute` | Marks key property |
| `VectorStoreDataAttribute` | Marks data property |
| `VectorStoreVectorAttribute` | Marks vector property |
| `VectorSearchOptions<TRecord>` | Filter + search options |
| `VectorSearchResult<TRecord>` | Search result with score |

Reference: https://learn.microsoft.com/dotnet/ai/conceptual/mevd-library

---

## April 2025 Update — Built-in Embedding Generation

The April 2025 update introduced **embedding generation directly within the VectorStore**. By configuring an `IEmbeddingGenerator` on the store options, embeddings are automatically generated for vector properties during upsert — no need to precompute them externally.

### Configuration levels

1. **On the VectorStore** — default generator for all collections and properties
2. **On a Collection** — overrides store-level generator for a specific collection
3. **On a Record Definition** — via `VectorStoreCollectionDefinition`
4. **On a Vector Property** — via `VectorStoreVectorProperty`

```csharp
// Store-level embedding generator
var vectorStore = new QdrantVectorStore(
    new QdrantClient("localhost"),
    new QdrantVectorStoreOptions
    {
        EmbeddingGenerator = embeddingGenerator
    });
```

### Impact on CodeMemory

Currently `IndexingEngine` (src/CodeMemory/Services/IndexingEngine.cs:173-195) manually:
1. Calls `embeddingGenerator.GenerateAsync(contents)` for all chunks
2. L2-normalizes each vector
3. Stores normalized vectors via `StoreChunksAsync`

The April 2025 update lets us delegate embedding generation to the VectorStore itself. This simplifies the IndexingEngine and is especially relevant for the AspNet path where a production VectorStore (PostgresVectorStore, SqlServerVectorStore) can handle auto-embedding.

For the Mcp path (InMemoryVectorStore from Memori), manual embedding may still be needed depending on Memori's version of `InMemoryVectorStore`.

Reference: https://learn.microsoft.com/semantic-kernel/support/migration/vectorstore-april-2025

---

## May 2025 — API Renames (Pre-GA)

The API was formalized before GA with renames affecting current code:

| Old Name | New Name |
|---|---|
| `IVectorStore` | `VectorStore` (abstract class) |
| `IVectorStoreRecordCollection` | `VectorStoreCollection` (abstract class) |
| `VectorStoreRecordDefinition` | `VectorStoreCollectionDefinition` |
| `VectorStoreRecordKeyAttribute` | `VectorStoreKeyAttribute` |
| `VectorStoreRecordDataAttribute` | `VectorStoreDataAttribute` |
| `VectorStoreRecordVectorAttribute` | `VectorStoreVectorAttribute` |
| `GetRecordOptions` | `RecordRetrievalOptions` |
| `GetFilteredRecordOptions<TRecord>` | `FilteredRecordRetrievalOptions<TRecord>` |
| `IVectorSearch<TRecord>` | `IVectorSearchable<TRecord>` |
| `IKeywordHybridSearch<TRecord>` | `IKeywordHybridSearchable<TRecord>` |
| `CreateCollectionIfNotExistsAsync` | `EnsureCollectionExistsAsync` |
| `DeleteAsync` (collection) | `EnsureCollectionDeletedAsync` |

CodeMemory's current `ChunkRecord.Embedding` attribute uses the pre-rename attribute name `VectorStoreVector` (which was itself renamed from `VectorStoreRecordVectorAttribute`). All three names resolve to the same attribute via type forwarding in the MEVD package.

Reference: https://learn.microsoft.com/semantic-kernel/support/migration/vectorstore-may-2025

---

## Microsoft.Extensions.DataIngestion Pipeline

The `Microsoft.Extensions.DataIngestion` library provides a formal document-processing pipeline:

```
Reader (load documents)
  → DocumentProcessors (enrich at document level)
  → Chunker (split into chunks)
  → ChunkProcessors (enrich at chunk level: summary, sentiment, keywords)
  → Writer (store in VectorStore, auto-generates embeddings)
```

Key types:
- `IngestionDocument` — unified document representation (Markdown-centric)
- `IngestionDocumentReader` — load documents from files, streams, etc.
- `IngestionChunker<T>` / `SemanticChunker` / `HeaderChunker` — chunking strategies
- `IngestionChunkWriter<T>` — stores chunks, with `VectorStoreWriter<T>` implementation
- `IngestionPipeline<T>` — chains everything together

```csharp
using IngestionPipeline<string> pipeline = new(reader, chunker, writer, loggerFactory)
{
    DocumentProcessors = { imageAlternativeTextEnricher },
    ChunkProcessors = { summaryEnricher }
};

await foreach (var result in pipeline.ProcessAsync(new DirectoryInfo("."), searchPattern: "*.md"))
{
    Console.WriteLine($"Completed '{result.DocumentId}'. Succeeded: '{result.Succeeded}'.");
}
```

### Relevance to CodeMemory

This library is still evolving (preview status as of May 2026). It is **not yet ready to replace** the IndexingEngine. However, its architecture validates CodeMemory's direction:

- **Separation of extraction, chunking, and storage** — CodeMemory already does this
- **Partial success handling** — single document failure shouldn't fail the whole pipeline
- **Writer abstraction** — the `VectorStoreWriter` pattern aligns with CodeMemory's `IStorageService`

**Decision:** Monitor this library. When it stabilizes, the IndexingEngine could be refactored to use `IngestionPipeline` with custom readers (file crawler), processors (symbol extraction, chunking), and writers (HybridStorageService). Not a priority now.

Reference: https://learn.microsoft.com/dotnet/ai/conceptual/data-ingestion

---

## Provider Ecosystem

Despite "SemanticKernel" in package names, these connectors are independent of Semantic Kernel and work with plain `Microsoft.Extensions.VectorData`:

| Provider | Package | Status |
|---|---|---|
| In-Memory | `Microsoft.SemanticKernel.Connectors.InMemory` | Development only |
| SQLite | `Microsoft.SemanticKernel.Connectors.SqliteVec` | Already referenced |
| PostgreSQL (pgvector) | `Microsoft.SemanticKernel.Connectors.PgVector` | Already referenced |
| SQL Server | `Microsoft.SemanticKernel.Connectors.SqlServer` | Already referenced |
| Azure AI Search | `Microsoft.SemanticKernel.Connectors.AzureAISearch` | Not yet |
| Qdrant | `Microsoft.SemanticKernel.Connectors.Qdrant` | Not yet |
| CosmosDB | `Microsoft.SemanticKernel.Connectors.CosmosNoSQL` | Not yet |

Microsoft's guidance: **Use InMemory for prototyping, swap for production.** However, avoid using InMemory for tests — behavior differs from production stores. Use testcontainers instead.

---

## Key Takeaways for CodeMemory

1. **Auto-embedding is the future** — the VectorStore should handle embedding generation, not the IndexingEngine. Ready for AspNet; Mcp depends on Memori's InMemoryVectorStore support.
2. **DataIngestion is aspirational** — the pipeline pattern aligns with our architecture, but the library is too early to adopt. Revisit in H2 2026.
3. **VectorStore is for vectors** — structured data (symbols, relationships) belongs in a relational database. This is the core insight driving `docs/STORAGE.md`.
4. **Mcp stays manual** — the InMemoryVectorStore doesn't support auto-embedding. The current manual approach (generate + L2-normalize + store) is correct for that path.
