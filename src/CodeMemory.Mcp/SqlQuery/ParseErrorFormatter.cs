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
        long line;
        long column;

        if (ex is ParserException parserEx)
        {
            message = parserEx.Message;
            line = parserEx.Line;
            column = parserEx.Column;
        }
        else if (ex is TokenizeException tokenizeEx)
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

        // The library reports Line/Column == 0 for some structural errors (e.g.
        // a trailing comma before FROM). Locate the offending token textually so
        // the agent still gets a line reference and caret.
        if (!hasContent)
        {
            var located = LocateOffendingToken(sql, clean);
            if (located is not null)
            {
                line = located.Value.Line;
                column = located.Value.Column;
                (codeLine, hasContent) = GetLine(sql, line);
            }
        }

        var caret = hasContent ? new string(' ', Math.Max(0, (int)column - 1)) + "^" : "";
        var positioned = hasContent
            ? $"Parse error at line {line}, column {column}:\n{codeLine}\n{caret}\n{clean}"
            : $"Parse error at line {line}, column {column}:\n{clean}";

        var tip = hasContent ? StraySemicolonTip(sql, line, column, clean) : null;
        return tip is null ? positioned : $"{positioned}\n{tip}";
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

    /// <summary>Locates the offending token (from the sanitized message) in the SQL text.</summary>
    static (long Line, long Column)? LocateOffendingToken(string sql, string clean)
    {
        var token = ExtractFoundToken(clean);
        if (token is null || string.Equals(token, "EOF", StringComparison.OrdinalIgnoreCase))
            return null;

        var index = FindWord(sql, token);
        if (index < 0)
            return null;

        return (LineOf(sql, index), ColumnOf(sql, index));
    }

    static string? ExtractFoundToken(string message)
    {
        var match = Regex.Match(message,
            @"found:?\s+(?:identifier\s+)?'([^']+)'|found\s*:?\s*([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    /// <summary>Finds the first whole-word, case-insensitive occurrence of the token.</summary>
    static int FindWord(string sql, string token)
    {
        var index = 0;
        while ((index = sql.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(sql[index - 1]);
            var after = index + token.Length >= sql.Length || !char.IsLetterOrDigit(sql[index + token.Length]);
            if (before && after)
                return index;

            index += token.Length;
        }

        return -1;
    }

    static int LineOf(string sql, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < sql.Length; i++)
            if (sql[i] == '\n') line++;

        return line;
    }

    static int ColumnOf(string sql, int index)
        => index - sql[..Math.Min(index, sql.Length)].LastIndexOf('\n');

    static int OffsetOf(string sql, long line, long column)
    {
        if (line < 1)
            return -1;

        var lines = sql.Split('\n');
        if (line > lines.Length)
            return -1;

        var offset = 0;
        for (var i = 0; i < line - 1; i++)
            offset += lines[i].Length + 1;

        return offset + Math.Max(0, (int)column - 1);
    }

    /// <summary>
    /// "Expected a SQL statement, found X" after a stray ';' means the parser treated the
    /// semicolon as a statement terminator and rejected the remainder — tell the agent why.
    /// </summary>
    static string? StraySemicolonTip(string sql, long line, long column, string clean)
    {
        if (!clean.StartsWith("Expected a SQL statement", StringComparison.OrdinalIgnoreCase))
            return null;

        var offset = OffsetOf(sql, line, column);
        if (offset <= 0)
            return null;

        var prefix = StripStringLiterals(sql[..offset]);
        var strayIndex = prefix.LastIndexOf(';');
        if (strayIndex < 0)
            return null;

        return $"Tip: found a stray ';' on line {LineOf(prefix, strayIndex)} before the reported position — " +
               "the parser treats ';' as a statement terminator. Remove it (or place it only at the very end of the query).";
    }

    /// <summary>Blanks out string literals (preserving offsets incl. newlines) so ';' inside them is ignored.</summary>
    static string StripStringLiterals(string text)
        => Regex.Replace(text, "'(?:[^'\\\\]|\\\\.|'')*'|\"(?:[^\"\\\\]|\\\\.|\"\")*\"",
            m => new string(' ', m.Length));

    /// <summary>Strips sqlparser-cs message quirks (doubled "Expected", Rust debug token dumps, embedded positions).</summary>
    public static string Sanitize(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var result = Regex.Replace(message, @"\bExpected\s+Expected\b", "Expected", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"Identifier\s*\{\s*Ident\s*=\s*([^}]+?)\s*\}", "identifier '$1'");
        result = Regex.Replace(result, @"Keyword\s*\(\s*([^)]+?)\s*\)", "$1");
        result = Regex.Replace(result, @"Word\s*\{\s*value:\s*([^,]+?)[,}].*?\}", "'$1'");
        result = Regex.Replace(result, @"\w+\s*\{[^{}]*\}", "value");
        result = Regex.Replace(result, @"(?:,?\s*Line:\s*\d+)(?:\s*,\s*Col:\s*\d+)?$", "");

        return result;
    }
}