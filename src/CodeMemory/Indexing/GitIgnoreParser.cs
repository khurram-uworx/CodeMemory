using System.Text.RegularExpressions;

namespace CodeMemory.Indexing;

/// <summary>
/// Parses gitignore-format rules from a single source (a <c>.gitignore</c> file or a set of
/// product-level exclude patterns) and evaluates paths relative to that source's directory,
/// following the semantics defined by <c>git-scm.com/docs/gitignore</c>:
/// <list type="bullet">
/// <item>a pattern without a slash matches the basename at any depth below the source directory;</item>
/// <item>a pattern with a leading or middle slash is anchored to the source directory;</item>
/// <item>a trailing slash marks the pattern directory-only: it matches the directory and everything
/// under it, but never a file with the exact same name;</item>
/// <item>the last matching pattern wins, so a <c>!</c> negation can re-include a path and a later
/// pattern can re-ignore it again;</item>
/// <item><c>**</c> spans directories, <c>*</c> and <c>?</c> never cross <c>/</c>, <c>[a-z]</c>
/// character classes are supported, and <c>\</c> escapes the next character.</item>
/// </list>
/// Matching is case-insensitive (Windows-friendly; matches the previous behavior of this class).
/// Trailing-space escaping (<c>foo\ </c>) and <c>.git/info/exclude</c> are known limitations.
/// </summary>
public sealed class GitIgnoreParser
{
    sealed record Rule(bool Negated, bool DirOnly, Regex Pattern, Regex? DescendantsOnly)
    {
        public bool Matches(string path, bool isDir)
        {
            if (!DirOnly)
                return Pattern.IsMatch(path);

            // Dir-only: the directory itself (or a nested directory under it) matches;
            // a file only matches when it lives strictly inside the directory.
            return isDir ? Pattern.IsMatch(path) : DescendantsOnly?.IsMatch(path) == true;
        }
    }

    readonly Rule[] rules;

    GitIgnoreParser(Rule[] rules)
    {
        this.rules = rules;
    }

    public static GitIgnoreParser Empty { get; } = new([]);

    public static GitIgnoreParser Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty;

    public static GitIgnoreParser FromPatterns(IEnumerable<string> patterns)
        => Parse([.. patterns]);

    public static GitIgnoreParser Parse(string[] lines)
    {
        var rules = new List<Rule>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var negated = line.StartsWith('!');
            if (negated)
                line = line[1..];

            // Escaped leading '#' or '!' is a literal character.
            if (line.Length > 1 && line[0] == '\\' && (line[1] == '#' || line[1] == '!'))
                line = line[1..];

            line = trimTrailingSpaces(line);
            if (line.Length == 0)
                continue;

            var dirOnly = line.EndsWith('/');
            if (dirOnly)
                line = line[..^1];

            if (line.Length == 0)
                continue;

            var anchored = line.StartsWith('/');
            if (anchored)
                line = line[1..];
            else if (!line.StartsWith("**/") && containsUnescapedSeparator(line))
                anchored = true;

            var rule = compileRule(negated, anchored, dirOnly, line);
            if (rule != null)
                rules.Add(rule);
        }

        return new GitIgnoreParser([.. rules]);
    }

    /// <summary>
    /// Returns whether <paramref name="relativePath"/> is ignored by this rule set.
    /// </summary>
    /// <param name="relativePath">Path relative to this parser's source directory, using '/' separators.</param>
    /// <param name="isDir">Whether the path is a directory. Required for dir-only pattern semantics.</param>
    public bool IsIgnored(string relativePath, bool isDir = false)
        => tryGetOutcome(relativePath, isDir) ?? false;

    /// <summary>
    /// Like <see cref="IsIgnored"/>, but returns <c>null</c> when no rule matches so callers that
    /// layer multiple rule sets (e.g. nested <c>.gitignore</c> files) can apply last-match-wins across
    /// layers.
    /// </summary>
    internal bool? tryGetOutcome(string relativePath, bool isDir)
    {
        var path = normalize(relativePath);
        if (path.Length == 0)
            return null;

        bool? last = null;
        foreach (var rule in rules)
        {
            if (rule.Matches(path, isDir))
                last = !rule.Negated;
        }
        return last;
    }

    static Rule? compileRule(bool negated, bool anchored, bool dirOnly, string pattern)
    {
        var baseRegex = buildBaseRegex(pattern, anchored);
        if (baseRegex is null)
            return null;

        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Compiled;
        var patternRegex = new Regex("^" + baseRegex + (dirOnly ? "(?:/.*)?$" : "$"), options);
        Regex? descendantsOnly = dirOnly ? new Regex("^" + baseRegex + "/.+$", options) : null;

        return new Rule(negated, dirOnly, patternRegex, descendantsOnly);
    }

    /// <summary>
    /// Produces the anchored regex source matching <paramref name="pattern"/> (without leading '^'
    /// or trailing '$'). Unanchored patterns get an any-depth prefix; globstars are expanded.
    /// </summary>
    static string? buildBaseRegex(string pattern, bool anchored)
    {
        var sb = new System.Text.StringBuilder();
        if (!anchored)
            sb.Append("(?:^|.*/)");

        int i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];

            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    var starStart = i;
                    i += 2;

                    if (i < pattern.Length && pattern[i] == '/')
                    {
                        // "**/" — zero or more directory segments
                        i++;
                        sb.Append("(?:[^/]+/)*");
                    }
                    else if (i >= pattern.Length)
                    {
                        // Trailing "**". Directly after a slash ("abc/**") it matches everything
                        // inside that directory; otherwise (per git) it degrades to a regular
                        // asterisk because "**" adjacent to a non-slash is not a globstar.
                        var afterSlash = starStart > 0 && pattern[starStart - 1] == '/';
                        sb.Append(afterSlash ? ".*" : "[^/]*");
                    }
                    else
                    {
                        // "**" adjacent to a non-slash is treated as regular asterisks (per git).
                        sb.Append("[^/]*[^/]*");
                    }

                    continue;
                }

                sb.Append("[^/]*");
                i++;
                continue;
            }

            if (c == '?')
            {
                sb.Append("[^/]");
                i++;
                continue;
            }

            if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end == -1)
                {
                    sb.Append("\\[");
                    i++;
                }
                else
                {
                    sb.Append(pattern, i, end - i + 1);
                    i = end + 1;
                }

                continue;
            }

            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                i += 2;
                continue;
            }

            sb.Append(Regex.Escape(c.ToString()));
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Trailing spaces are ignored unless escaped by a backslash (git behavior).
    /// </summary>
    static string trimTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            if (end > 1 && line[end - 2] == '\\')
                break;
            end--;
        }
        return line[..end];
    }

    /// <summary>
    /// Returns true when <paramref name="pattern"/> contains an unescaped '/' at a position other
    /// than the first character. Per gitignore(5), a separator in the middle anchors the pattern to
    /// the directory of the .gitignore file; a leading "**/" is the documented exception and stays
    /// unanchored (it matches in all directories).
    /// </summary>
    static bool containsUnescapedSeparator(string pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\')
            {
                i++;
                continue;
            }

            if (pattern[i] == '/' && i > 0)
                return true;
        }

        return false;
    }

    static string normalize(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return path.TrimStart('/');
    }
}