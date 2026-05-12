# CodeMemory + Memori — Convergence Future

## 1. Vision

> A persistent, queryable, semantic memory layer for software systems.

**CodeMemory** provides the deterministic repository intelligence (symbols, dependencies, architecture, git analysis).  
**Memori** provides the durable memory substrate (capture, augmentation, recall, versioning, vector search).

Together they form a feedback loop where CodeMemory generates structured intelligence about a codebase and Memori stores it as queryable memory — including design rationale extracted from git history, semantic facts about components, and conversation context from developers discussing the code.

## 2. Shared Infrastructure Boundary

Long-term division of concerns:

| Belongs in **Memori** (NuGet) | Belongs in **CodeMemory** (MCP server) |
|---|---|
| `IEmbeddingGenerator` implementations (n-gram, deterministic, future ones) | Repository scanning / crawling |
| `IChatClient` middleware / pipelines | Roslyn parsing + symbol extraction |
| Vector store abstractions (`IVectorStore` usage) | Dependency graph construction |
| Augmentation pipeline (`IAugmentationClient`) | Architecture inference + component clustering |
| Capture / recall / versioning | Git history analysis |
| Conversation storage | MCP tool definitions |
| `IThreadSummarizer` | `IStorageService` (SQLiteVec-backed) |

### Currently shared (already aligned)

- `Microsoft.Extensions.AI` abstractions (`IEmbeddingGenerator<string, Embedding<float>>`, `IChatClient`)
- `Microsoft.Extensions.VectorData` abstractions
- `System.Numerics.Tensors` for L2 normalization
- `.NET 10` target framework
- `.editorconfig`, coding conventions, project layout patterns

### Already moved (completed)

- `NgramEmbeddingGenerator` → Memori's `Embeddings/` namespace (`Memori` NuGet package v0.2.2+)

---

## 3. Memori Augmentation on Repo Files — Smart Strategies

Running LLM-based augmentation on every file in a repo is cost-prohibitive. Smart approximation strategies:

### 3a. Hotspot Targeting

Use CodeMemory's `get_hotspots` to identify the most-changed files. Augment only those. Files that rarely change and have few dependents likely don't need semantic fact extraction.

### 3b. Hub-First

Use CodeMemory's `trace_dependency(downstream)` to find files with the most dependents (hubs). Augment the hub, and the facts implicitly cover everything that depends on it. Pareto principle: ~20% of files explain ~80% of the codebase.

### 3c. Cluster Sampling

Use CodeMemory's `get_component_clusters` to group components by coupling density. Pick representative files from each cluster rather than every file.

### 3d. Delta-Only

Track which files already have stored facts (via versioning metadata in the vector store). Only augment new or changed files since the last indexing pass.

### Implementation sketch

```csharp
// Pseudo-code for smart file augmentation during indexing
var hotspots = await gitHistoryService.GetHotspotsAsync(top: 50);
var hubs = await dependencyGraphService.GetTopHubsAsync(top: 30);
var clusters = await componentClusteringService.GetClustersAsync();

var targetFiles = hotspots.Concat(hubs).Distinct();

foreach (var file in targetFiles)
{
    var symbols = await symbolQueryService.GetSymbolsInFile(file);
    var analysis = await impactAnalysisService.AnalyzeFileAsync(file);

    // Feed as pseudo-messages to Memori's capture pipeline
    var userMsg = new ChatMessage(ChatRole.User,
        $"File: {file}\nSymbols: {string.Join(", ", symbols.Select(s => s.Name))}\n" +
        $"Kind: {symbols.First().Kind}\nImpact: {analysis.AffectedFiles.Count} dependents");

    var asstMsg = new ChatMessage(ChatRole.Assistant,
        $"Indexed {symbols.Count} symbols in {file}");

    await memori.CaptureAsync([userMsg, asstMsg]);
}
```

---

## 4. Git History Augmentation — Design Rationale Memory

This is the most novel capability: using Memori's augmentation to extract *design rationale* from commits.

### 4a. The Pipeline

```
git commit → CodeMemory impact analysis → Memori pseudo-messages → augmentation → stored facts
```

CodeMemory provides:
- Which symbols changed (via symbol diff)
- How many files were touched
- What downstream dependents are affected (via `impact_analysis`)

Memori augmentation extracts:
- What the change means semantically
- Design rationale hidden in the commit message + structural context
- Cross-commit patterns (e.g., "PaymentGateway has been refactored 3 times")

### 4b. The Pseudo-Message Pattern

The key insight: Memori's existing `CaptureAsync` → augmentation pipeline works with *any* `ChatMessage` pairs. CodeMemory generates pseudo-messages that look like a conversation between the codebase (User) and the indexing system (Assistant):

```csharp
// For git history
var userMsg = new ChatMessage(ChatRole.User,
    $"commit {hash}\nmessage: {commitMessage}\n" +
    $"files changed: {files.Count}\n" +
    $"symbols affected: {string.Join(", ", symbols)}\n" +
    $"impact: {dependents.Count} downstream dependents");

var asstMsg = new ChatMessage(ChatRole.Assistant,
    $"Captured design rationale for commit {hashShort}");

await memori.CaptureAsync([userMsg, asstMsg]);
// → Augmentation extracts: "In commit abc123, PaymentGateway was refactored
//   from 3 to 2 constructor params to decouple invoicing"
```

```csharp
// For file content during scanning
var userMsg = new ChatMessage(ChatRole.User,
    $"symbol {symbol.Name} in {symbol.FilePath}\n" +
    $"kind: {symbol.Kind}\n" +
    $"implements: {string.Join(", ", symbol.Interfaces)}\n" +
    $"dependents: {dependents.Count} downstream");

var asstMsg = new ChatMessage(ChatRole.Assistant,
    $"Indexed symbol {symbol.Name}");

await memori.CaptureAsync([userMsg, asstMsg]);
// → Augmentation extracts: "PaymentGateway is a class at src/Gateway.cs
//   implementing IPaymentGateway, depended on by 5 callers"
```

### 4c. What This Enables

At query time, a developer or AI agent can ask:

> "How does payment work here?" / "Why was retry logic removed?" / "What's the history of auth?"

And get back facts extracted from both:
- The **static structure** (CodeMemory's MCP tools)
- The **design rationale** (Memori's recalled facts from git history augmentation)

This is the "evolution layer" from `IDEA.md` brought to life.

---

## 5. Versioning & Re-Indexing

Memori already has record versioning with last-write-wins and merge conflict resolution. This maps naturally to re-indexing:

- When a file changes, its old facts are superseded by new ones
- When a commit is amended, the old rationale fact is replaced
- The `IThreadSummarizer` can roll up repeated augmentations into a summary
- No special conflict logic needed — Memori's existing versioning layer handles it

---

## 6. Open Questions / Future Exploration

- **Augmentation client specialization**: Memori's `IAugmentationClient` currently assumes conversation context. A `CodeAugmentationClient` with prompts tuned for code/git analysis would produce higher quality facts than the generic LLM prompt.
- **Fact deduplication**: If the same fact is extracted from both file scanning and git history, how to merge/prioritize?
- **Cost budgeting**: What's the $/repo tradeoff for LLM augmentation across a large monorepo? The smart strategies in §3 mitigate this but need real-world validation.
- **Fact freshness**: When a file hasn't changed but a dependent has, do stored facts about the file become stale? Should the fact store track dependency invalidation?

---

## 7. Not Sharing — Boundary Analysis

Each candidate below was evaluated for moving into Memori and rejected. Documented to preserve the reasoning.

| Candidate | Why it stays in CodeMemory |
|---|---|
| `IMemoryRanker` / distributed ranker | Memori's ranker fuses vector + lexical scores for conversation recall. CodeMemory ranks via SQLiteVec `ORDER BY distance` — different mechanism, different domain. |
| `IConversationStorage` | Conversation persistence interface. CodeMemory has no conversations. |
| `IStorageService` (SQLiteVec-backed) | Code-specific storage for symbols, chunks, relationships. No abstraction overlap with Memori's storage contracts. |
| Vector store provider choice | Memori uses `Microsoft.Extensions.VectorData` abstractions (provider-agnostic). CodeMemory intentionally uses SQLiteVec directly for its schema and query needs. Abstracting would add indirection with no multi-provider requirement. |
| DI / logging / config patterns | These are project conventions, not shareable code. Already aligned via `.editorconfig`, `AGENTS.md`, and `ARCHITECTURE.md`. |
| MCP tool definitions / server | CodeMemory exposes MCP tools for repository intelligence. Memori is a library — it has no MCP server. Forcing one in would violate Memori's "no cloud/service coupling" guardrail. |
| Chat middleware (`MemoriChatClient`) | CodeMemory doesn't route messages through `IChatClient`. The middleware pipeline is specific to chat applications. |
| `IAugmentationClient` | Memori's augmentation assumes conversational context. CodeMemory would need a specialized `CodeAugmentationClient` — the *interface* lives in Memori, the *implementation* is codebase-specific. |
| `IThreadSummarizer` | Summarization of conversation threads. Not applicable to code indexing or git history. |
| `IMemoryManagementService` | Manage stored memories (list, search, edit, soft-delete). If CodeMemory needs this, it would need its own management layer over its own storage schema — not the same interface. |
| Roslyn parsing / syntax analysis | Deeply code-specific. Memori has no use for C# AST parsing. |
| `DeterministicEmbeddingGenerator` (64-dim) | Already trivial (~70 lines). If CodeMemory ever wants it, adding the Memori NuGet ref covers it. No need to actively move it. |

**Verdict:** The shared surface is small because the two projects address different domains (general AI memory vs. repository intelligence). `IEmbeddingGenerator<string, Embedding<float>>` is the genuine intersection — handled via the Memori NuGet package.

---

## 8. Relationship to Existing Docs

| Doc | Connection |
|---|---|
| `IDEA.md` | High-level vision of "persistent codebase brain" — FUTURE.md is the concrete integration architecture |
| `AGENTS.md` | Engineering constraints for implementing the patterns described here |
| `ARCHITECTURE.md` | Current system architecture — FUTURE.md extends the data flow to include Memori as a peer |
