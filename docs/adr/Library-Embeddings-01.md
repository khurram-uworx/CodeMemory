# ADR-Library-Embeddings-01: NgramEmbeddingGenerator as Default Embedding Strategy

---

## Context

CodeMemory uses `IEmbeddingGenerator<string, Embedding<float>>` from `Microsoft.Extensions.AI` for all vector embedding needs — indexing source code chunks and embedding search queries for semantic search. The interface is injectable and swappable via DI registration in `Program.cs`.

Both hosts register the same default implementation:

| Host | Registration |
|---|---|
| `CodeMemory.Mcp` (STDIO) | `AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, NgramEmbeddingGenerator>()` |
| `CodeMemory.AspNet` | Same, in `Program.cs` |

The implementation comes from the `Memori` NuGet package (`Memori.Embeddings` namespace). Its source lives at [`Memori/src/Memori/Embeddings/NgramEmbeddingGenerator.cs`](https://github.com/khurram-uworx/Memori).

### How it works

The generator is a purely algorithmic, deterministic character n-gram embedding:

1. Collapse whitespace, guard for text < 2 chars
2. Extract all character n-grams of lengths [2, 3, 4]
3. Hash each n-gram via `hash * 31 + char` (polynomial rolling hash)
4. Multi-hash (4 hashes per n-gram, combined with `HashCode.Combine`) into 1536 buckets with ±1 contribution
5. L2-normalize the resulting vector

Key properties:

| Property | Value |
|---|---|
| **Vector dimension** | 1536 (fixed) |
| **Determinism** | Yes — same input → same vector across processes, sessions, and machines |
| **External state** | None — no model files, network, random seeds, or configuration |
| **Computational cost** | O(n) in text length, pure CPU |
| **NuGet package** | `Memori` ≥ 0.2.2 |

---

## Decision

**Keep NgramEmbeddingGenerator as the default embedding implementation.** Do not require a real ML-based embedding model (OpenAI, Azure AI, local ONNX, etc.) as a prerequisite for running CodeMemory.

---

## Rationale

### 1. Deterministic cross-session consistency (fatal if violated)

CodeMemory.AspNet's indexing and querying may run in separate process lifetimes — indexing happens in `IndexingHostedService` at startup, while MCP tools serve queries later (possibly in a new process after restart). An embedding generator with external state (model file on disk, network service, random seed) would risk:

- Different vectors for the same text when loaded in a different process
- Model-load failures blocking search while indexing succeeded
- Version skew between the model used at index-time vs query-time

`NgramEmbeddingGenerator` is a pure function — it has none of these failure modes. The same text always produces the same 1536-dimensional vector, regardless of when or where `GenerateAsync` is called.

### 2. Zero startup cost

ML embedding models add 1–10 seconds of startup time (model loading, GPU warmup, auth handshake). CodeMemory's MCP servers must respond to `ping` immediately — blocking startup on model loading is unacceptable. The n-gram approach adds ~0ms.

### 3. No external dependencies

No API keys, no network connectivity, no model files to distribute, no GPU required. This keeps the project self-contained and easy to run in CI/dev/test environments.

### 4. Adequate for code search

Code search is fundamentally a **lexical** problem, not a semantic one:

- Developers search for identifiers, type names, keywords — terms that literally appear in the code
- Identifiers follow naming conventions (`camelCase`, `PascalCase`, `snake_case`) that produce high n-gram overlap for related names (`getUserById` / `getUserByName`)
- Language keywords are shared across all files
- Comments and documentation use project-specific terminology

For this domain, n-gram similarity approximates meaningful relatedness surprisingly well — much better than random vectors, and often good enough for top-10 retrieval in a codebase.

### 5. Swappable via DI

Users who need true semantic understanding can replace the generator at registration time:

```csharp
// In Program.cs — replace the default:
builder.Services.AddSingleton<
    IEmbeddingGenerator<string, Embedding<float>>,
    OpenAIEmbeddingGenerator>(); // or Azure, ONNX, etc.
```

The `CodeMemory.AspNet.Extensions` project provides two ONNX-based alternatives as reference implementations:

| Provider | Config value | Implementation | Dependencies |
|---|---|---|---|
| **Raw ONNX Runtime** | `"onnx"` | `BertOnnxEmbeddingGenerator` — custom `IEmbeddingGenerator` using `Microsoft.ML.OnnxRuntime` directly, showing tokenization, inference, mean pooling, and L2 normalization | `Microsoft.ML.OnnxRuntime` |
| **SK ONNX Connector** | `"sk-connector-onnx"` | Uses `AddBertOnnxEmbeddingGenerator()` from `Microsoft.SemanticKernel.Connectors.Onnx` — production-grade wrapper | `Microsoft.SemanticKernel.Connectors.Onnx` |

Both require the [bge-micro-v2](https://huggingface.co/TaylorAI/bge-micro-v2) ONNX model (~69MB, 384-dim), downloadable via `download-models.ps1`. Configure via `appsettings.json`:

```json
{
  "Embedding": {
    "Provider": "onnx",
    "OnnxModelPath": "models/bge-micro-v2/model.onnx",
    "OnnxVocabPath": "models/bge-micro-v2/vocab.txt"
  }
}
```

All downstream code (`IndexingEngine`, `SemanticSearchService`, `SqlQueryService`) works unchanged because they depend only on the `IEmbeddingGenerator` abstraction.

---

## Consequences

### Positive

- Zero-friction onboarding — no model setup needed
- Fast startup in both hosts
- Cross-process/session consistency guaranteed
- All storage providers (inmemory, sqlite, pgvector, sqlserver) work identically
- Easy to swap for ML-based generator later

### Negative (limitations)

The library-embedding approach has **real, measurable quality gaps** compared to ML-based embeddings:

| Scenario | Ngram behavior | Practical impact |
|---|---|---|
| **Synonyms** | `"delete"` vs `"remove"` → no n-gram overlap → low similarity | Searching for "delete user" won't find `RemoveUser()` |
| **Concept matching** | `"authentication"` vs `"login"` → no common trigrams | Natural-language queries fail if vocabulary differs from identifiers |
| **Abstract queries** | `"find slow code"` → doesn't match `PerformantQuery` or comments about complexity | Only matches literal terms |
| **Cross-language concepts** | C# `List` and JS `Array` → no shared n-grams | No cross-language semantic bridging |
| **Short queries** | `< 2 chars` → zero vector → no results | Very short queries are invisible |

### Trade-off accepted

The simplicity and robustness of algorithmic embeddings outweigh the quality loss for the default case. Power users who need synonym-aware, concept-level search should configure an ML embedding generator.

### Agent expectation management

Coding agents that consume CodeMemory's MCP tools must understand that `semantic_search` and `sql_query` with `ORDER BY SIMILARITY` / `VECTOR_SEARCH` are **lexical-overlap searches**, not true semantic searches. The following expectations must be set in `AGENTS.md`:

- Results are biased toward literal text matches — queries should use terminology that appears in the codebase
- Synonym-based queries will miss relevant code
- `minimumSimilarity` thresholds may need to be lower (e.g., 0.3–0.5) compared to ML embeddings
- The tool works best when query terms are drawn from observed identifiers, types, and comments

---

## Compliance

- The `IEmbeddingGenerator` abstraction MUST remain the sole embedding interface — no code outside `Program.cs` should reference `NgramEmbeddingGenerator` directly.
- Default DI registration MUST remain `NgramEmbeddingGenerator` unless a specific embedding model is configured.
- All embedding quality documentation (`AGENTS.md`, tool descriptions) MUST reference these limitations.
- A future ADR may revisit defaults if a lightweight, zero-dependency ML embedding becomes feasible (e.g., ONNX-based BERT distilled to < 10MB).

---

## Alternatives considered

| Alternative | Rejected because |
|---|---|
| **OpenAI / Azure OpenAI embeddings** | Requires API key, network, billing; adds startup latency; breaks offline/dev scenarios; DI-swappable so not a replacement for the default |
| **ONNX local model (BERT-mini/LaBSE)** | Adds 5–50MB binary + ~2s load time; overkill for default; can be added via DI later |
| **Raw ONNX Runtime custom implementation** | Educational but heavier; implemented in `CodeMemory.AspNet.Extensions` as `BertOnnxEmbeddingGenerator` — demonstrates the full pipeline (tokenization → inference → pooling → normalization) for learning purposes |
| **DeterministicEmbeddingGenerator** (64-dim, word-hash based, same Memori namespace) | Lower quality than n-gram; 64-dim limits downstream vector-store compatibility; 1536 maintains compatibility with standard embedding dimensions |
| **Skip embeddings entirely (keyword-only search)** | Rejected — even crude vector search (n-gram) outperforms bag-of-words for ranking; dimension reduction would lose too much signal |
