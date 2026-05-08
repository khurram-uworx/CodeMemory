using CodeMemory.Indexing.Extraction;

namespace CodeMemory.Indexing.Chunking;

public sealed record DocumentChunk(
    string Id,
    string SymbolId,
    string FilePath,
    string Content,
    string Language,
    LineRange LineRange,
    IReadOnlyDictionary<string, string> Metadata);
