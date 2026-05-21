using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace CodeMemory.Indexing;

public sealed partial class GitIgnoreParser
{
    static bool matchLiteral(string path, FrozenSet<string> literals)
    {
        foreach (var literal in literals)
        {
            if (string.Equals(path, literal, StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.Contains('/' + literal, StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.StartsWith(literal, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == literal.Length || path[literal.Length] == '/'))
                return true;
        }

        return false;
    }

    static bool isSimpleLiteral(string pattern)
    {
        return !pattern.Contains('*') && !pattern.Contains('?') && !pattern.Contains('[');
    }

    static string globToRegex(string pattern)
    {
        var regex = new System.Text.StringBuilder("^");

        if (!pattern.Contains('/'))
        {
            regex.Append("(.*/)?");
        }
        else if (pattern.StartsWith('/'))
        {
            pattern = pattern[1..];
        }
        else
        {
            regex.Append("(.*/)?");
        }

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                            i++;
                        regex.Append(".*");
                    }
                    else
                    {
                        regex.Append("[^/]*");
                    }
                    break;
                case '?':
                    regex.Append('.');
                    break;
                case '.':
                case '+':
                case '^':
                case '$':
                case '{':
                case '}':
                case '|':
                case '(':
                case ')':
                    regex.Append('\\').Append(c);
                    break;
                default:
                    regex.Append(c);
                    break;
            }
        }

        regex.Append('$');
        return regex.ToString();
    }

    public static GitIgnoreParser Empty { get; } = new(
        FrozenSet<string>.Empty,
        FrozenSet<string>.Empty,
        []);

    public static GitIgnoreParser Load(string gitignorePath)
    {
        if (!File.Exists(gitignorePath))
            return Empty;

        var lines = File.ReadAllLines(gitignorePath);
        return Parse(lines);
    }

    public static GitIgnoreParser Parse(string[] lines)
    {
        var literalIgnores = new List<string>();
        var literalNegations = new List<string>();
        var regexPatterns = new List<Regex>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            bool negate = line.StartsWith('!');
            if (negate)
                line = line[1..];

            line = line.TrimEnd('/');

            if (isSimpleLiteral(line))
            {
                line = line.TrimStart('/');

                if (negate)
                    literalNegations.Add(line);
                else
                    literalIgnores.Add(line);
            }
            else
            {
                var pattern = globToRegex(line);
                regexPatterns.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
        }

        return new GitIgnoreParser(
            literalIgnores.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            literalNegations.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            [.. regexPatterns]);
    }

    readonly FrozenSet<string> literalNegations;
    readonly FrozenSet<string> literalIgnores;
    readonly Regex[] regexPatterns;

    GitIgnoreParser(
        FrozenSet<string> literalIgnores,
        FrozenSet<string> literalNegations,
        Regex[] regexPatterns)
    {
        this.literalIgnores = literalIgnores;
        this.literalNegations = literalNegations;
        this.regexPatterns = regexPatterns;
    }

    public bool IsIgnored(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');

        // Negation patterns take precedence
        if (matchLiteral(normalized, literalNegations))
            return false;

        foreach (var regex in regexPatterns)
        {
            if (regex.IsMatch(normalized))
                return true;
        }

        return matchLiteral(normalized, literalIgnores);
    }
}
