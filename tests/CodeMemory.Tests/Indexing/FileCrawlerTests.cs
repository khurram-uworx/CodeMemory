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
            f.RelativePath.StartsWith("bin/") ||
            f.RelativePath.StartsWith("obj/") ||
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

    static string createTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryFileCrawlerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    static async Task<IReadOnlyList<FileEntry>> walkCsFiles(string root)
    {
        var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance, allowedExtensions: [".cs"]);
        return await crawler.WalkAsync(root).ToListAsync();
    }

    [Test]
    public async Task WalkAsync_ExcludesNestedNodeModules()
    {
        var root = createTempRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b", "node_modules"));
            File.WriteAllText(Path.Combine(root, "a", "b", "node_modules", "Noise.cs"), "public class Noise {}");
            File.WriteAllText(Path.Combine(root, "plain.cs"), "public class Plain {}");

            var files = await walkCsFiles(root);

            Assert.That(files.Select(f => f.RelativePath), Does.Not.Contain("a/b/node_modules/Noise.cs"));
            Assert.That(files.Select(f => f.RelativePath), Does.Contain("plain.cs"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task WalkAsync_NestedGitIgnore_UnignoresFile()
    {
        var root = createTempRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.cs\n");
            File.WriteAllText(Path.Combine(root, "app", ".gitignore"), "!keep.cs\n");
            File.WriteAllText(Path.Combine(root, "app", "keep.cs"), "public class Keep {}");
            File.WriteAllText(Path.Combine(root, "app", "drop.cs"), "public class Drop {}");

            var files = await walkCsFiles(root);

            Assert.That(files.Select(f => f.RelativePath), Does.Contain("app/keep.cs"));
            Assert.That(files.Select(f => f.RelativePath), Does.Not.Contain("app/drop.cs"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task WalkAsync_AdditionalExclusionGlob_ExcludesNestedBin()
    {
        var root = createTempRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "bin"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "a", "bin", "Generated.cs"), "public class Generated {}");
            File.WriteAllText(Path.Combine(root, "src", "HandWritten.cs"), "public class HandWritten {}");

            var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance, allowedExtensions: [".cs"]);
            var files = await crawler.WalkAsync(root, additionalExclusions: new HashSet<string>(["**/bin/**"])).ToListAsync();

            Assert.That(files.Select(f => f.RelativePath), Does.Not.Contain("a/bin/Generated.cs"));
            Assert.That(files.Select(f => f.RelativePath), Does.Contain("src/HandWritten.cs"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task WalkAsync_DefaultGitIgnore_AppliesToNestedDirs()
    {
        var root = createTempRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b", "target"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "target/\n");
            File.WriteAllText(Path.Combine(root, "a", "b", "target", "Generated.cs"), "public class Generated {}");
            File.WriteAllText(Path.Combine(root, "src", "HandWritten.cs"), "public class HandWritten {}");

            var files = await walkCsFiles(root);

            Assert.That(files.Select(f => f.RelativePath), Does.Not.Contain("a/b/target/Generated.cs"));
            Assert.That(files.Select(f => f.RelativePath), Does.Contain("src/HandWritten.cs"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
