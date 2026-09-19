using SqlParser;
using System.Text.RegularExpressions;

namespace CodeMemory.Mcp.SqlQuery;

/// <summary>
/// Turns parser/tokenizer exceptions into actionable messages: a position (line/column),
/// a snippet of the offending SQL line with a caret, and a sanitized reason.
/// sqlparser-cs prefixes "Expected" twice at Parser.cs:6886 and dumps tokens in Rust
/// debug form (Identifier { Ident = FROM }); this formatter works around both (see
/// AGENTS.md §SQL Parser Development Reference) without patching the library.
/// </summary>
public static class ParseErrorFormatter
{
    /// <summary>Formats a parser/tokenizer exception into a positioned, sanitized message.</summary>
    public static string Format(string sql, Exception ex)
    {
        string message;
        long line = 0;
        long column = 0;

        if (ex is ParserException { Line: > 0 } parseEx)
        {
            message = parseEx.Message;
            line = parseEx.Line;
            column = parseEx.Column;
        }
        else if (ex is TokenizeException { Line: > 0 } tokenizeEx)
        {
            message = tokenizeEx.Message;
            line = tokenizeEx.Line;
            column = tokenizeEx.Column;
        }
        else
        {
            return $"Parse error: {Sanitize(ex.Message)}";
        }

        var clean = Sanitize(message);
        var (codeLine, hasContent) = GetLine(sql, line);
        var caret = hasContent ? new string(' ', Math.Max(0, (int)column - 1)) + "^" : "";

        return hasContent
            ? $"Parse error at line {line}, column {column}:\n{codeLine}\n{caret}\n{clean}"
            : $"Parse error at line {line}, column {column}:\n{clean}";
    }

    static (string Line, bool HasContent) GetLine(string sql, long line)
    {
        if (line < 1)
            return ("", false);

        var lines = sql.Split('\n');
        if (line > lines.Length)
            return ("", false);

        return (lines[line - 1].TrimEnd(), true);
    }

    /// <summary>Strips sqlparser-cs message quirks (doubled "Expected", Rust debug token dumps).</summary>
    public static string Sanitize(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var result = Regex.Replace(message, @"\bExpected\s+Expected\b", "Expected", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"Identifier\s*\{\s*Ident\s*=\s*([^}]+?)\s*\}", "identifier '$1'");
        result = Regex.Replace(result, @"Keyword\s*\(\s*([^)]+?)\s*\)", "$1");
        result = Regex.Replace(result, @"Word\s*\{\s*value:\s*([^,]+?)[,}].*?\}", "'$1'");
        result = Regex.Replace(result, @"\w+\s*\{[^{}]*\}", "value");

        return result;
    }
}