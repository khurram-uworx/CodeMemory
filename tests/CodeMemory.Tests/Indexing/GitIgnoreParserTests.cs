using CodeMemory.Indexing;

namespace CodeMemory.Tests.Indexing;

public sealed class GitIgnoreParserTests
{
    [Test]
    public void IsIgnored_BareName_MatchesAtAnyDepth()
    {
        var parser = GitIgnoreParser.Parse(["node_modules"]);

        Assert.That(parser.IsIgnored("node_modules"), Is.True);
        Assert.That(parser.IsIgnored("a/node_modules"), Is.True);
        Assert.That(parser.IsIgnored("a/b/node_modules"), Is.True);
        Assert.That(parser.IsIgnored("node_modulesX"), Is.False);
        Assert.That(parser.IsIgnored("a/node_modulesX"), Is.False);
    }

    [Test]
    public void IsIgnored_AnchoredSlash_MatchesOnlyWithinAnchorDir()
    {
        var parser = GitIgnoreParser.Parse(["/build"]);

        Assert.That(parser.IsIgnored("build"), Is.True);
        Assert.That(parser.IsIgnored("a/build"), Is.False);

        parser = GitIgnoreParser.Parse(["sub/build"]);

        Assert.That(parser.IsIgnored("sub/build"), Is.True);
        Assert.That(parser.IsIgnored("other/sub/build"), Is.False);
        Assert.That(parser.IsIgnored("build"), Is.False);
    }

    [Test]
    public void IsIgnored_BareDirOnly_MatchesNestedDirectoriesAndDescendants()
    {
        var parser = GitIgnoreParser.Parse(["node_modules/"]);

        Assert.That(parser.IsIgnored("node_modules", isDir: true), Is.True);
        Assert.That(parser.IsIgnored("a/b/node_modules", isDir: true), Is.True);
        Assert.That(parser.IsIgnored("a/b/node_modules/pkg/x.js", isDir: false), Is.True);
        Assert.That(parser.IsIgnored("node_modules", isDir: false), Is.False,
            "A dir-only pattern must never ignore a file with the same name");
    }

    [Test]
    public void IsIgnored_AnchoredDirOnly_MatchesRootDirAndDescendantsOnly()
    {
        var parser = GitIgnoreParser.Parse(["/bin/"]);

        Assert.That(parser.IsIgnored("bin", isDir: true), Is.True);
        Assert.That(parser.IsIgnored("bin/x.dll", isDir: false), Is.True);
        Assert.That(parser.IsIgnored("a/bin", isDir: true), Is.False);
        Assert.That(parser.IsIgnored("bin", isDir: false), Is.False);
    }

    [Test]
    public void IsIgnored_LastMatchingRuleWins_NegationReincludes()
    {
        var parser = GitIgnoreParser.Parse(["*.log", "!keep.log"]);

        Assert.That(parser.IsIgnored("keep.log"), Is.False);
        Assert.That(parser.IsIgnored("other.log"), Is.True);

        parser = GitIgnoreParser.Parse(["!keep.log", "*.log"]);

        Assert.That(parser.IsIgnored("keep.log"), Is.True,
            "A later ignore pattern re-ignores a path negated earlier");
    }

    [Test]
    public void IsIgnored_DoubleStar_TrailingMatchesEverythingInside()
    {
        var parser = GitIgnoreParser.Parse(["docs/**"]);

        Assert.That(parser.IsIgnored("docs/a.md"), Is.True);
        Assert.That(parser.IsIgnored("docs/a/b.md"), Is.True);
        Assert.That(parser.IsIgnored("other/docs/a.md"), Is.False);
    }

    [Test]
    public void IsIgnored_DoubleStar_MiddleMatchesZeroOrMoreDirs()
    {
        var parser = GitIgnoreParser.Parse(["a/**/b"]);

        Assert.That(parser.IsIgnored("a/b"), Is.True);
        Assert.That(parser.IsIgnored("a/x/b"), Is.True);
        Assert.That(parser.IsIgnored("a/x/y/b"), Is.True);
        Assert.That(parser.IsIgnored("a/b/c"), Is.False);
        Assert.That(parser.IsIgnored("ax/b"), Is.False);
    }

    [Test]
    public void IsIgnored_DoubleStar_LeadingMatchesAtAnyDepth()
    {
        var parser = GitIgnoreParser.Parse(["**/foo.cs"]);

        Assert.That(parser.IsIgnored("foo.cs"), Is.True);
        Assert.That(parser.IsIgnored("a/foo.cs"), Is.True);
        Assert.That(parser.IsIgnored("a/b/foo.cs"), Is.True);
    }

    [Test]
    public void IsIgnored_QuestionMark_MatchesSingleNonSlashChar()
    {
        var parser = GitIgnoreParser.Parse(["a?c.txt"]);

        Assert.That(parser.IsIgnored("abc.txt"), Is.True);
        Assert.That(parser.IsIgnored("a/c.txt"), Is.False);
        Assert.That(parser.IsIgnored("ac.txt"), Is.False);
    }

    [Test]
    public void IsIgnored_CharClass_MatchesAnyInSet()
    {
        var parser = GitIgnoreParser.Parse(["[Bb]in"]);

        Assert.That(parser.IsIgnored("bin"), Is.True);
        Assert.That(parser.IsIgnored("Bin"), Is.True);
        Assert.That(parser.IsIgnored("cin"), Is.False);
    }

    [Test]
    public void IsIgnored_EscapedComment_IsTreatedAsLiteral()
    {
        var parser = GitIgnoreParser.Parse(["\\#file.txt"]);

        Assert.That(parser.IsIgnored("#file.txt"), Is.True);
        Assert.That(parser.IsIgnored("other.txt"), Is.False);
    }

    [Test]
    public void IsIgnored_EscapedBang_IsTreatedAsLiteral()
    {
        var parser = GitIgnoreParser.Parse(["\\!important.txt"]);

        Assert.That(parser.IsIgnored("!important.txt"), Is.True);
        Assert.That(parser.IsIgnored("important.txt"), Is.False);
    }

    [Test]
    public void IsIgnored_CommentsAndBlankLines_AreSkipped()
    {
        var parser = GitIgnoreParser.Parse([
            "# a comment",
            "",
            "   ",
            "node_modules",
        ]);

        Assert.That(parser.IsIgnored("a/node_modules", isDir: true), Is.True);
        Assert.That(parser.IsIgnored("anything.txt"), Is.False);
    }

    [Test]
    public void IsIgnored_TrailingSpaces_AreIgnored()
    {
        var parser = GitIgnoreParser.Parse(["build "]);

        Assert.That(parser.IsIgnored("build", isDir: true), Is.True);
    }

    [Test]
    public void IsIgnored_EmptyParser_IgnoresNothing()
    {
        Assert.That(GitIgnoreParser.Empty.IsIgnored("anything.txt"), Is.False);
        Assert.That(GitIgnoreParser.Empty.IsIgnored("a/node_modules/x.js"), Is.False);
    }

    [Test]
    public void FromPatterns_BehavesLikeParse()
    {
        var parser = GitIgnoreParser.FromPatterns(["*.txt", "**/bin/**"]);

        Assert.That(parser.IsIgnored("a.txt"), Is.True);
        Assert.That(parser.IsIgnored("src/a/bin/b.txt"), Is.True);
        Assert.That(parser.IsIgnored("src/a/bin/b.cs"), Is.True);
    }
}