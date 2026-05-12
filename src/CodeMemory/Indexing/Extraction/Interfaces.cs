using CodeMemory.Indexing.Parsing;

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
    Function,
    Variable,
    TypeAlias,
    Module,
    Constructor,
    Annotation,
    Decorator,
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

public sealed record Relationship(
    string SourceSymbolId,
    string TargetSymbolId,
    string RelationshipType);

public interface IRelationshipExtractor
{
    IReadOnlyList<Relationship> ExtractRelationships(
        ParseResult result, IReadOnlyList<Symbol> symbols, string filePath);
}

public interface ISymbolExtractor
{
    IReadOnlyList<Symbol> Extract(ParseResult result, string filePath);
}
