using CodeMemory.Indexing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing;

public sealed class FileCrawlerTests
{
    static readonly string repoRoot = findRepoRoot();

    static string findRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, ".gitignore")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    [Test]
    public async Task WalkAsync_WithNoFilter_ReturnsAllFiles()
    {
        var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance);
        var files = await crawler.WalkAsync(repoRoot).ToListAsync();

        Assert.That(files, Is.Not.Empty);
        Assert.That(files, Has.All.Matches<FileEntry>(f =>
            !string.IsNullOrEmpty(f.Path) &&
            !string.IsNullOrEmpty(f.RelativePath)));
    }

    [Test]
    public async Task WalkAsync_WithCsExtensionFilter_ReturnsOnlyCsFiles()
    {
        var crawler = new FileCrawler(
            NullLogger<FileCrawler>.Instance,
            allowedExtensions: [".cs"]);
        var files = await crawler.WalkAsync(repoRoot).ToListAsync();

        Assert.That(files, Is.Not.Empty);
        Assert.That(files, Has.All.Matches<FileEntry>(f => f.Extension == ".cs"));
    }

    [Test]
    public async Task WalkAsync_RespectsGitIgnorePatterns()
    {
        var parser = GitIgnoreParser.Parse([
            "bin/",
            "obj/",
            "*.txt"
        ]);

        var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance);
        var files = await crawler.WalkAsync(repoRoot, ignoreParser: parser).ToListAsync();

        Assert.That(files, Has.None.Matches<FileEntry>(f =>
            f.RelativePath.StartsWith("bin") ||
            f.RelativePath.StartsWith("obj") ||
            f.RelativePath.EndsWith(".txt")));
    }

    [Test]
    public async Task WalkAsync_UsesDefaultGitIgnoreWhenPresent()
    {
        var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance);
        var files = await crawler.WalkAsync(repoRoot).ToListAsync();

        Assert.That(files, Has.None.Matches<FileEntry>(f =>
            f.RelativePath.StartsWith("bin\\") ||
            f.RelativePath.StartsWith("bin/") ||
            f.RelativePath.StartsWith("obj\\") ||
            f.RelativePath.StartsWith("obj/") ||
            f.RelativePath.StartsWith(".git\\") ||
            f.RelativePath.StartsWith(".git/")));
    }

    [Test]
    public async Task WalkAsync_ErrorOnInvalidDirectory_DoesNotThrow()
    {
        var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance);
        var parser = GitIgnoreParser.Empty;

        var files = await crawler.WalkAsync(
            "Z:\\nonexistent\\path\\that\\does\\not\\exist",
            ignoreParser: parser).ToListAsync();

        Assert.That(files, Is.Empty);
    }
}
