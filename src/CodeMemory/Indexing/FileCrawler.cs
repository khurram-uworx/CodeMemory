using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace CodeMemory.Indexing;

public sealed record FileEntry(
    string Path,
    string RelativePath,
    string Extension,
    DateTime LastModified);

public sealed class FileCrawler
{
    static string getRelativePath(Uri rootUri, string fullPath)
    {
        var fileUri = new Uri(fullPath);
        var relative = rootUri.MakeRelativeUri(fileUri).ToString();
        return Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
    }

    private static GitIgnoreParser loadGitIgnore(string rootPath)
    {
        var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
        return GitIgnoreParser.Load(gitIgnorePath);
    }

    static bool isDirIgnored(string? relDir, GitIgnoreParser ignoreParser)
    {
        if (string.IsNullOrEmpty(relDir))
            return false;

        var dirName = relDir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (dirName != null && alwaysIgnored.Contains(dirName))
            return true;

        return ignoreParser.IsIgnored(relDir + "/") || ignoreParser.IsIgnored(relDir);
    }

    static bool isFileIgnored(string relPath, GitIgnoreParser ignoreParser)
    {
        return ignoreParser.IsIgnored(relPath);
    }

    static readonly FrozenSet<string> alwaysIgnored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git"
    }.ToFrozenSet();

    readonly ILogger<FileCrawler> logger;
    readonly HashSet<string> allowedExtensions;

    public FileCrawler(ILogger<FileCrawler> logger, HashSet<string>? allowedExtensions = null)
    {
        this.logger = logger;
        this.allowedExtensions = allowedExtensions ?? [];
    }

    public async IAsyncEnumerable<FileEntry> WalkAsync(
        string rootPath,
        GitIgnoreParser? ignoreParser = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        rootPath = Path.GetFullPath(rootPath);
        ignoreParser ??= loadGitIgnore(rootPath);

        var rootUri = new Uri(rootPath + Path.DirectorySeparatorChar);

        var directories = new Queue<string>();
        directories.Enqueue(rootPath);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = directories.Dequeue();
            var relDir = getRelativePath(rootUri, dir);

            if (isDirIgnored(relDir, ignoreParser))
            {
                logger.LogDebug("Skipping ignored directory: {Dir}", relDir);
                continue;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                logger.LogWarning(ex, "Cannot access directory: {Dir}", dir);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                directories.Enqueue(subDir);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                logger.LogWarning(ex, "Cannot access files in directory: {Dir}", dir);
                continue;
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ext = Path.GetExtension(filePath);
                if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(ext))
                    continue;

                var relPath = getRelativePath(rootUri, filePath);

                if (isFileIgnored(relPath, ignoreParser))
                {
                    logger.LogDebug("Skipping ignored file: {File}", relPath);
                    continue;
                }

                FileEntry entry;
                try
                {
                    var info = new FileInfo(filePath);
                    entry = new FileEntry(filePath, relPath, ext, info.LastWriteTimeUtc);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Cannot read file info: {File}", filePath);
                    continue;
                }

                yield return entry;
            }
        }
    }
}
