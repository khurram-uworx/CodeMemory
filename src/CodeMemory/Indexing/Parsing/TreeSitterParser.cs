using Microsoft.Extensions.Logging;
using TreeSitter;

namespace CodeMemory.Indexing.Parsing;

public sealed class TreeSitterParser : ILanguageParser
{
    static readonly Dictionary<Language, TreeSitter.Language> languageMap = new()
    {
        [Language.TypeScript] = new TreeSitter.Language("TypeScript"),
        [Language.JavaScript] = new TreeSitter.Language("JavaScript"),
        [Language.Java] = new TreeSitter.Language("Java"),
        [Language.Python] = new TreeSitter.Language("Python"),
        [Language.Go] = new TreeSitter.Language("Go"),
        [Language.Rust] = new TreeSitter.Language("Rust"),
        [Language.HTML] = new TreeSitter.Language("HTML"),
    };

    readonly ILogger<TreeSitterParser> logger;

    public TreeSitterParser(ILogger<TreeSitterParser> logger)
        => this.logger = logger;

    public async Task<ParseResult?> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var lang = LanguageDetector.Detect(filePath);
        if (lang == Parsing.Language.Unknown)
            return null;

        if (!languageMap.TryGetValue(lang, out var tsLanguage))
            return null;

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
            using var parser = new Parser(tsLanguage);
            var tree = parser.Parse(text);
            if (tree == null)
            {
                logger.LogWarning("Tree-sitter returned null tree for: {FilePath}", filePath);
                return null;
            }

            logger.LogDebug("Successfully parsed: {FilePath} ({Language})", filePath, lang);
            return new ParseResult(text, lang, null, tree);
        }
        catch (DllNotFoundException ex)
        {
            logger.LogError(ex, "Tree-sitter native DLLs not available on this platform");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse file with tree-sitter: {FilePath}", filePath);
            return null;
        }
    }
}
