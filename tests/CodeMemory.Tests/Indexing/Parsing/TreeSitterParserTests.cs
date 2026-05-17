using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Parsing;

public sealed class TreeSitterParserTests
{
    static bool isTreeSitterAvailable()
    {
        try
        {
            using var parser = new TreeSitter.Parser(new TreeSitter.Language("TypeScript"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Test]
    public async Task ParseAsync_WithTypeScriptFile_ReturnsParseResultWithTsTree()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");

        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.ts");
        try
        {
            await File.WriteAllTextAsync(path, "class Foo { bar: string; }");

            var result = await parser.ParseAsync(path);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TsTree, Is.Not.Null);
            Assert.That(result.Language, Is.EqualTo(Language.TypeScript));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ParseAsync_WithJavaScriptFile_ReturnsParseResultWithTsTree()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");

        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.js");
        try
        {
            await File.WriteAllTextAsync(path, "function foo() { return 42; }");

            var result = await parser.ParseAsync(path);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TsTree, Is.Not.Null);
            Assert.That(result.Language, Is.EqualTo(Language.JavaScript));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ParseAsync_WithJavaFile_ReturnsParseResultWithTsTree()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");

        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.java");
        try
        {
            await File.WriteAllTextAsync(path, "class Foo { void bar() {} }");

            var result = await parser.ParseAsync(path);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TsTree, Is.Not.Null);
            Assert.That(result.Language, Is.EqualTo(Language.Java));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ParseAsync_WithUnknownExtension_ReturnsNull()
    {
        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.rb");
        try
        {
            await File.WriteAllTextAsync(path, "def foo; end");

            var result = await parser.ParseAsync(path);

            Assert.That(result, Is.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ParseAsync_WithPythonFile_ReturnsParseResultWithTsTree()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");

        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.py");
        try
        {
            await File.WriteAllTextAsync(path, "class Foo:\n    def bar(self):\n        pass");

            var result = await parser.ParseAsync(path);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TsTree, Is.Not.Null);
            Assert.That(result.Language, Is.EqualTo(Language.Python));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ParseAsync_WithHtmlFile_ReturnsParseResultWithTsTree()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");

        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.html");
        try
        {
            await File.WriteAllTextAsync(path, "<html><body><p>Hello</p></body></html>");

            var result = await parser.ParseAsync(path);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TsTree, Is.Not.Null);
            Assert.That(result.Language, Is.EqualTo(Language.HTML));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ParseAsync_WithMalformedTypeScript_ReturnsTree()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");

        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.ts");
        try
        {
            await File.WriteAllTextAsync(path, "class Foo { broken syntax @@@ !!! }");

            var result = await parser.ParseAsync(path);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TsTree, Is.Not.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
