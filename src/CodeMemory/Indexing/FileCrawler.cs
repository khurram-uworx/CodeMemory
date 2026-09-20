using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace CodeMemory.Indexing;

public sealed record FileEntry(
    string Path,
    string RelativePath,
    string Extension,
    DateTime LastModified);

public sealed class FileCrawler
{
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
        Action<double>? onProgress = null,
        IReadOnlySet<string>? additionalExclusions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        rootPath = Path.GetFullPath(rootPath);

        string[]? extraPatterns = additionalExclusions?.Count > 0
            ? [.. additionalExclusions.Select(e => e.Replace('\\', '/'))]
            : null;

        var evaluator = ignoreParser != null
            ? GitIgnoreEvaluator.FromRootParser(rootPath, ignoreParser, extraPatterns)
            : new GitIgnoreEvaluator(rootPath, extraPatterns);

        var rootUri = new Uri(rootPath + Path.DirectorySeparatorChar);

        var directories = new Queue<(string Path, double Weight)>();
        directories.Enqueue((rootPath, 1.0));
        double completed = 0.0;

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (dir, weight) = directories.Dequeue();

            try
            {
                var relDir = getRelativePath(rootUri, dir);

                if (isDirIgnored(relDir, evaluator))
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

                var filteredSubDirs = new List<string>(subDirs.Length);
                foreach (var subDir in subDirs)
                {
                    var subRelDir = getRelativePath(rootUri, subDir);
                    if (!isDirIgnored(subRelDir, evaluator))
                        filteredSubDirs.Add(subDir);
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

                var fileList = new List<string>(files.Length);
                foreach (var filePath in files)
                {
                    var ext = Path.GetExtension(filePath);
                    if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(ext))
                        continue;

                    var relPath = getRelativePath(rootUri, filePath);
                    if (!isFileIgnored(relPath, evaluator))
                        fileList.Add(filePath);
                }

                double childCount = fileList.Count + filteredSubDirs.Count;

                foreach (var filePath in fileList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileEntry entry;
                    try
                    {
                        var info = new FileInfo(filePath);
                        var relPath = getRelativePath(rootUri, filePath);
                        var ext = Path.GetExtension(filePath);
                        entry = new FileEntry(filePath, relPath, ext, info.LastWriteTimeUtc);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Cannot read file info: {File}", filePath);
                        continue;
                    }

                    var fileWeight = childCount > 0 ? weight / childCount : weight;
                    completed += fileWeight;
                    onProgress?.Invoke(completed);

                    yield return entry;
                }

                foreach (var subDir in filteredSubDirs)
                {
                    var subDirWeight = childCount > 0 ? weight / childCount : weight;
                    directories.Enqueue((subDir, subDirWeight));
                }
            }
            finally { }
        }
    }

    static bool isDirIgnored(string? relDir, GitIgnoreEvaluator evaluator)
    {
        if (string.IsNullOrEmpty(relDir))
            return false;

        return evaluator.IsIgnored(relDir, isDir: true);
    }

    static bool isFileIgnored(string? relPath, GitIgnoreEvaluator evaluator)
    {
        if (string.IsNullOrEmpty(relPath))
            return false;

        return evaluator.IsIgnored(relPath, isDir: false);
    }

    static string getRelativePath(Uri rootUri, string fullPath)
    {
        var fileUri = new Uri(fullPath);
        var relative = rootUri.MakeRelativeUri(fileUri).ToString();
        return Uri.UnescapeDataString(relative);
    }
}