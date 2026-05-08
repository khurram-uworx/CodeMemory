namespace CodeMemory.Indexing.Extraction;

public enum CodeSymbolKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Record,
    Method,
    Property,
    Field,
    Event,
}

public sealed record LineRange(int Start, int End);

public sealed record Symbol(
    string Name,
    CodeSymbolKind Kind,
    string FilePath,
    LineRange LineRange,
    string FullName,
    IReadOnlyList<string> Modifiers,
    string? Documentation = null);
