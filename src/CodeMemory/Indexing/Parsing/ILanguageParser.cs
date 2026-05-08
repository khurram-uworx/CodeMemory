using Microsoft.CodeAnalysis;

namespace CodeMemory.Indexing.Parsing;

public interface ILanguageParser
{
    Task<SyntaxTree?> ParseAsync(string filePath, CancellationToken cancellationToken = default);
}
