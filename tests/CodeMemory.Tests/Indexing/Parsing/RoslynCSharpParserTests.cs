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

        var result = await parser.ParseAsync(filePath);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.RoslynTree, Is.Not.Null);
        Assert.That(result.RoslynTree!.GetRoot(), Is.Not.Null);
        Assert.That(result.Language, Is.EqualTo(Language.CSharp));
    }

    [Test]
    public async Task ParseAsync_WithNonExistentFile_ReturnsNull()
    {
        var parser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);

        var result = await parser.ParseAsync("Z:\\nonexistent\\file.cs");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ParseAsync_WithMalformedContent_ReturnsSyntaxTreeWithErrors()
    {
        var parser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);
        var malformedPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(malformedPath, "this is not valid csharp ###");

            var result = await parser.ParseAsync(malformedPath);

            Assert.That(result, Is.Not.Null);
            var diagnostics = result!.RoslynTree!.GetDiagnostics();
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

            var result = await parser.ParseAsync(malformedPath);

            Assert.That(result, Is.Not.Null);
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

            var result = await parser.ParseAsync(emptyPath);

            Assert.That(result, Is.Not.Null);
        }
        finally
        {
            File.Delete(emptyPath);
        }
    }
}
