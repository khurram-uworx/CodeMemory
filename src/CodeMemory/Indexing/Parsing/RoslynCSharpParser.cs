using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeMemory.Indexing.Parsing;

public sealed class RoslynCSharpParser : ILanguageParser
{
    readonly ILogger<RoslynCSharpParser> logger;

    public RoslynCSharpParser(ILogger<RoslynCSharpParser> logger)
    {
        this.logger = logger;
    }

    public async Task<SyntaxTree?> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Parsing file: {FilePath}", filePath);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to read file: {FilePath}", filePath);
            return null;
        }

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(text, path: filePath, cancellationToken: cancellationToken);
            var diagnostics = syntaxTree.GetDiagnostics(cancellationToken);

            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                logger.LogWarning(
                    "Parsed {FilePath} with {ErrorCount} error(s) — first error: {FirstError}",
                    filePath, errors.Count, errors[0].GetMessage());
            }
            else
            {
                logger.LogDebug("Successfully parsed: {FilePath}", filePath);
            }

            return syntaxTree;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse file: {FilePath}", filePath);
            return null;
        }
    }
}
