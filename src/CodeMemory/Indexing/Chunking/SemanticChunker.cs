using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace CodeMemory.Indexing.Chunking;

public sealed class SemanticChunker
{
    static string ExtractFileContext(string[] fileLines)
    {
        var context = new StringBuilder();

        foreach (var line in fileLines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("using ") || trimmed.StartsWith("namespace "))
            {
                context.AppendLine(line.TrimEnd('\r'));
            }
            else if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("///"))
            {
                // skip file-level comments
                continue;
            }
            else if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }
            else
            {
                break;
            }
        }

        return context.ToString().TrimEnd();
    }

    static DocumentChunk CreateTypeChunk(
        Symbol symbol,
        string[] fileLines,
        string fileContext,
        string filePath,
        Language language)
    {
        var typeLines = ExtractLines(fileLines, symbol.LineRange);
        var content = new StringBuilder();

        if (!string.IsNullOrEmpty(fileContext))
        {
            content.AppendLine(fileContext);
            content.AppendLine();
        }

        content.Append(typeLines);

        var id = ComputeId(symbol.FullName, content.ToString(), filePath);
        var metadata = new Dictionary<string, string>
        {
            ["chunkType"] = "type",
            ["symbolKind"] = symbol.Kind.ToString(),
        };

        return new DocumentChunk(
            id, symbol.FullName, filePath, content.ToString(),
            language.ToString(), symbol.LineRange, metadata);
    }

    static DocumentChunk CreateMemberChunk(
        Symbol symbol,
        string[] fileLines,
        string filePath,
        Language language)
    {
        var memberLines = ExtractLines(fileLines, symbol.LineRange);
        var parentName = ExtractParentName(symbol.FullName);
        var content = new StringBuilder();

        if (parentName != null)
        {
            content.AppendLine($"// Parent: {parentName}");
        }

        content.Append(memberLines);

        var id = ComputeId(symbol.FullName, content.ToString(), filePath);
        var metadata = new Dictionary<string, string>
        {
            ["chunkType"] = "member",
            ["symbolKind"] = symbol.Kind.ToString(),
        };

        if (parentName != null)
        {
            metadata["parentName"] = parentName;
        }

        return new DocumentChunk(
            id, symbol.FullName, filePath, content.ToString(),
            language.ToString(), symbol.LineRange, metadata);
    }

    static string ExtractLines(string[] fileLines, LineRange range)
    {
        var sb = new StringBuilder();
        for (int i = range.Start - 1; i < range.End && i < fileLines.Length; i++)
        {
            sb.AppendLine(fileLines[i].TrimEnd('\r'));
        }
        return sb.ToString().TrimEnd();
    }

    static string? ExtractParentName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        if (lastDot <= 0)
            return null;

        // Check if the part after the last dot is a method signature with parentheses
        var afterDot = fullName[(lastDot + 1)..];
        if (afterDot.Contains('('))
        {
            // It's a method/property - parent is everything before the last dot
            return fullName[..lastDot];
        }

        return null;
    }

    static string ComputeId(string symbolId, string content, string filePath)
    {
        var input = $"{symbolId}|{content}|{filePath}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    readonly ILogger<SemanticChunker> logger;

    public SemanticChunker(ILogger<SemanticChunker> logger)
    {
        this.logger = logger;
    }

    public IReadOnlyList<DocumentChunk> ChunkAll(
        IReadOnlyList<Symbol> symbols,
        string fileText,
        string filePath,
        Language language)
    {
        var chunks = new List<DocumentChunk>();
        var fileLines = fileText.Split('\n');

        // Collect top-level types so we can skip their member-related chunks
        var typeSymbols = symbols
            .Where(s => s.Kind is CodeSymbolKind.Class or CodeSymbolKind.Interface
                or CodeSymbolKind.Struct or CodeSymbolKind.Enum or CodeSymbolKind.Record)
            .ToList();

        var memberSymbols = symbols
            .Where(s => s.Kind is CodeSymbolKind.Method or CodeSymbolKind.Property
                or CodeSymbolKind.Field or CodeSymbolKind.Event)
            .ToList();

        // File-level context (usings + namespace)
        var fileContext = ExtractFileContext(fileLines);

        foreach (var typeSymbol in typeSymbols)
        {
            var typeChunk = CreateTypeChunk(typeSymbol, fileLines, fileContext, filePath, language);
            chunks.Add(typeChunk);
        }

        foreach (var memberSymbol in memberSymbols)
        {
            var memberChunk = CreateMemberChunk(memberSymbol, fileLines, filePath, language);
            chunks.Add(memberChunk);
        }

        logger.LogDebug(
            "Chunked {File} — {TypeCount} types, {MemberCount} members, {ChunkCount} chunks, avg {AvgSize} chars",
            filePath, typeSymbols.Count, memberSymbols.Count, chunks.Count,
            chunks.Count > 0 ? chunks.Average(c => c.Content.Length) : 0);

        return chunks;
    }
}
