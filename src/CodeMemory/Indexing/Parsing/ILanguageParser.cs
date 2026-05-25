using Microsoft.CodeAnalysis;

namespace CodeMemory.Indexing.Parsing;

public sealed record ParseResult(
    string FileText,
    Language Language,
    SyntaxTree? RoslynTree,
    object? TsTree);

public interface ILanguageParser
{
    Task<ParseResult?> ParseAsync(string filePath, CancellationToken cancellationToken = default);
}
