using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Parsing;

public sealed class RoslynCSharpParserTests
{
    static readonly string fixturesDir = Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..",
        "CodeMemory.Tests", "fixtures");

    [Test]
    public async Task ParseAsync_WithValidCSharpFile_ReturnsSyntaxTree()
    {
        var parser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);
        var filePath = Path.Combine(fixturesDir, "SampleClass.cs");

        var syntaxTree = await parser.ParseAsync(filePath);

        Assert.That(syntaxTree, Is.Not.Null);
        Assert.That(syntaxTree!.GetRoot(), Is.Not.Null);
    }

    [Test]
    public async Task ParseAsync_WithNonExistentFile_ReturnsNull()
    {
        var parser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);

        var syntaxTree = await parser.ParseAsync("Z:\\nonexistent\\file.cs");

        Assert.That(syntaxTree, Is.Null);
    }

    [Test]
    public async Task ParseAsync_WithMalformedContent_ReturnsSyntaxTreeWithErrors()
    {
        var parser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);
        var malformedPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(malformedPath, "this is not valid csharp ###");

            var syntaxTree = await parser.ParseAsync(malformedPath);

            Assert.That(syntaxTree, Is.Not.Null);
            var diagnostics = syntaxTree!.GetDiagnostics();
            Assert.That(diagnostics, Has.Some.Matches<Microsoft.CodeAnalysis.Diagnostic>(d =>
                d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        }
        finally
        {
            File.Delete(malformedPath);
        }
    }

    [Test]
    public async Task ParseAsync_LogsWarningOnParseErrors()
    {
        var logger = new TestLogger<RoslynCSharpParser>();
        var parser = new RoslynCSharpParser(logger);
        var malformedPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(malformedPath, "class { invalid");

            var syntaxTree = await parser.ParseAsync(malformedPath);

            Assert.That(syntaxTree, Is.Not.Null);
            Assert.That(logger.Warnings, Has.Count.GreaterThan(0));
        }
        finally
        {
            File.Delete(malformedPath);
        }
    }

    [Test]
    public async Task ParseAsync_WithEmptyFile_ReturnsSyntaxTree()
    {
        var parser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);
        var emptyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(emptyPath, "");

            var syntaxTree = await parser.ParseAsync(emptyPath);

            Assert.That(syntaxTree, Is.Not.Null);
        }
        finally
        {
            File.Delete(emptyPath);
        }
    }
}
