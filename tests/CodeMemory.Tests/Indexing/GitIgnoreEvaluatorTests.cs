using CodeMemory.Indexing;

namespace CodeMemory.Tests.Indexing;

public sealed class GitIgnoreEvaluatorTests
{
    static string createTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryGitIgnoreTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void writeGitIgnore(string repoRoot, string relDir, string content)
    {
        var dir = relDir.Length == 0 ? repoRoot : Path.Combine(repoRoot, relDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), content);
    }

    [Test]
    public void IsIgnored_NestedGitIgnore_UnignoresOuterIgnore()
    {
        var root = createTempRepo();
        try
        {
            writeGitIgnore(root, "", "*.cs\n");
            writeGitIgnore(root, "app", "!keep.cs\n");

            var evaluator = new GitIgnoreEvaluator(root);

            Assert.That(evaluator.IsIgnored("app/keep.cs", isDir: false), Is.False);
            Assert.That(evaluator.IsIgnored("app/other.cs", isDir: false), Is.True);
            Assert.That(evaluator.IsIgnored("keep.cs", isDir: false), Is.True,
                "Outer ignore still applies where no nested rule exists");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void IsIgnored_NestedGitIgnore_DeepestLayerWins()
    {
        var root = createTempRepo();
        try
        {
            writeGitIgnore(root, "", "*.md\n");
            writeGitIgnore(root, "app", "!keep.md\n");
            writeGitIgnore(root, "app/sub", "keep.md\n");

            var evaluator = new GitIgnoreEvaluator(root);

            Assert.That(evaluator.IsIgnored("app/keep.md", isDir: false), Is.False);
            Assert.That(evaluator.IsIgnored("app/sub/keep.md", isDir: false), Is.True,
                "The deepest .gitignore re-ignores what a shallower file re-included");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void IsIgnored_ExtraRootPatterns_AppliedLast_OverrideNestedUnignore()
    {
        var root = createTempRepo();
        try
        {
            writeGitIgnore(root, "", "*.cs\n");
            writeGitIgnore(root, "app", "!keep.cs\n");

            var evaluator = new GitIgnoreEvaluator(root, ["**/keep.cs"]);

            Assert.That(evaluator.IsIgnored("app/keep.cs", isDir: false), Is.True,
                "Additive excludes win over .gitignore negation");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void IsIgnored_AlwaysIgnored_MatchesAtAnyDepth()
    {
        var root = createTempRepo();
        try
        {
            var evaluator = new GitIgnoreEvaluator(root);

            Assert.That(evaluator.IsIgnored("a/b/node_modules/x.cs", isDir: false), Is.True);
            Assert.That(evaluator.IsIgnored("x/.git/config", isDir: false), Is.True);
            Assert.That(evaluator.IsIgnored("a/node_modules", isDir: true), Is.True);
            Assert.That(evaluator.IsIgnored("plain.cs", isDir: false), Is.False);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void IsIgnored_DirItsOwnGitIgnore_DoesNotGovernItself()
    {
        var root = createTempRepo();
        try
        {
            writeGitIgnore(root, "app", "target/\n");

            var evaluator = new GitIgnoreEvaluator(root);

            Assert.That(evaluator.IsIgnored("app", isDir: true), Is.False);
            Assert.That(evaluator.IsIgnored("app/target", isDir: true), Is.True,
                "A directory's own .gitignore rules apply to what is inside it");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void IsIgnored_FileInsideIgnoredDir_IsIgnored()
    {
        var root = createTempRepo();
        try
        {
            writeGitIgnore(root, "", "target/\n");

            var evaluator = new GitIgnoreEvaluator(root);

            Assert.That(evaluator.IsIgnored("a/target/x.cs", isDir: false), Is.True);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void IsIgnored_FromRootParser_UsesInjectedRulesOnly()
    {
        var root = createTempRepo();
        try
        {
            writeGitIgnore(root, "", "*.cs\n");

            var evaluator = GitIgnoreEvaluator.FromRootParser(
                root, GitIgnoreParser.Parse(["*.md"]));

            Assert.That(evaluator.IsIgnored("x.md", isDir: false), Is.True);
            Assert.That(evaluator.IsIgnored("x.cs", isDir: false), Is.False,
                "Disk .gitignore files are ignored when a root parser is injected");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void IsIgnored_EmptyRepo_IgnoresNothing()
    {
        var root = createTempRepo();
        try
        {
            var evaluator = new GitIgnoreEvaluator(root);

            Assert.That(evaluator.IsIgnored("a/b/c.txt", isDir: false), Is.False);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}