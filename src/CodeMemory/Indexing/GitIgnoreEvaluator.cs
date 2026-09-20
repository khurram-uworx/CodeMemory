using System.Collections.Frozen;

namespace CodeMemory.Indexing;

/// <summary>
/// Evaluates ignore rules for paths under a repository root by layering every <c>.gitignore</c>
/// file from the root down to the path's containing directory, plus always-ignored names and an
/// optional set of additive product-level exclude patterns (from <c>.codememory.json</c>
/// <c>exclude</c> or <c>rescan_repository</c>).
/// </summary>
/// <remarks>
/// Layering follows git precedence: deeper <c>.gitignore</c> files override outer ones, and the
/// last matching pattern wins across the whole stack. A path is <c>AlwaysIgnored</c> when any of
/// its segments matches a built-in name (<c>.git</c>, <c>.codememory</c>, <c>.memori</c>,
/// <c>.codememory.json</c>, <c>node_modules</c>) — at any depth, so nested dependency directories
/// never reach the index. Extra root patterns are a hard, additive layer applied after all
/// <c>.gitignore</c> files (a match always ignores; they cannot be negated). Directories inside an
/// ignored directory stay ignored — like git, a parent exclusion cannot be overridden for its
/// descendants, so callers may prune ignored directory subtrees eagerly.
/// </remarks>
public sealed class GitIgnoreEvaluator
{
    static readonly FrozenSet<string> AlwaysIgnored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".codememory", ".memori", ".codememory.json",
        "node_modules",
    }.ToFrozenSet();

    readonly string rootPath;
    readonly IReadOnlyList<GitIgnoreParser> extraLayers;
    readonly GitIgnoreParser? rootOverride;
    readonly Dictionary<string, IReadOnlyList<(string Dir, GitIgnoreParser Parser)>> layerCache = [];

    public GitIgnoreEvaluator(string rootPath, IReadOnlyList<string>? extraRootPatterns = null)
        : this(rootPath, extraRootPatterns, null)
    {
    }

    GitIgnoreEvaluator(string rootPath, IReadOnlyList<string>? extraRootPatterns, GitIgnoreParser? rootOverride)
    {
        this.rootPath = Path.GetFullPath(rootPath);
        this.rootOverride = rootOverride;
        extraLayers = extraRootPatterns is { Count: > 0 }
            ? [GitIgnoreParser.FromPatterns(extraRootPatterns)]
            : [];
    }

    /// <summary>
    /// Creates an evaluator that uses <paramref name="root"/> as the only file-derived layer and
    /// never reads <c>.gitignore</c> files from disk. Used by callers that inject a pre-built rule
    /// set (e.g. tests).
    /// </summary>
    public static GitIgnoreEvaluator FromRootParser(
        string rootPath, GitIgnoreParser root, IReadOnlyList<string>? extraRootPatterns = null)
        => new(rootPath, extraRootPatterns, root);

    public static bool IsAlwaysIgnored(string relPath)
        => hasAlwaysIgnoredSegment(normalize(relPath));

    public bool IsIgnored(string relPath, bool isDir)
    {
        var path = normalize(relPath);
        if (path.Length == 0)
            return false;

        if (hasAlwaysIgnoredSegment(path))
            return true;

        bool? last = null;
        foreach (var (dir, parser) in getLayers(parentDir(path)))
        {
            var rel = dir.Length == 0 ? path : path[(dir.Length + 1)..];
            var outcome = parser.tryGetOutcome(rel, isDir);
            if (outcome != null)
                last = outcome;
        }

        // Additive hard excludes applied after every .gitignore file — a match always ignores.
        if (extraLayers.Count > 0)
        {
            foreach (var extra in extraLayers)
                if (extra.IsIgnored(path, isDir))
                    return true;
        }

        return last ?? false;
    }

    /// <summary>
    /// Effective (outermost-to-innermost) rule layers for <paramref name="relDir"/>, each labeled
    /// with the directory its rules are relative to. A directory's own <c>.gitignore</c> governs
    /// what is inside it, so it is included in this list, and ancestors are computed recursively
    /// and memoized so a breadth-first crawl stays O(dirs).
    /// </summary>
    IReadOnlyList<(string Dir, GitIgnoreParser Parser)> getLayers(string relDir)
    {
        if (layerCache.TryGetValue(relDir, out var cached))
            return cached;

        IReadOnlyList<(string Dir, GitIgnoreParser Parser)> layers;
        if (relDir.Length == 0)
        {
            layers = rootOverride != null
                ? [("", rootOverride)]
                : readGitIgnore("") is { } rootParser ? [("", rootParser)] : [];
        }
        else
        {
            layers = [.. getLayers(parentDir(relDir))];

            if (rootOverride == null && readGitIgnore(relDir) is { } parser)
                layers = [.. layers, (relDir, parser)];
        }

        layerCache[relDir] = layers;
        return layers;
    }

    GitIgnoreParser? readGitIgnore(string relDir)
    {
        var dir = relDir.Length == 0
            ? rootPath
            : Path.Combine(rootPath, relDir.Replace('/', Path.DirectorySeparatorChar));
        return GitIgnoreParser.Load(Path.Combine(dir, ".gitignore"));
    }

    static bool hasAlwaysIgnoredSegment(string path)
        => path.Split('/').Any(segment => AlwaysIgnored.Contains(segment));

    static string parentDir(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    static string normalize(string relPath)
        => relPath.Replace('\\', '/').TrimStart('/');
}