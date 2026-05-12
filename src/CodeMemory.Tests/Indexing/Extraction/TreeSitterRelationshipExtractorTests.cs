using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Extraction;

public sealed class TreeSitterRelationshipExtractorTests
{
    static readonly string tempDir = Path.GetTempPath();

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

    static async Task<(IReadOnlyList<Symbol> Symbols, ParseResult Result, string FilePath)> extractFromCode(
        string code, string extension)
    {
        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var extractor = new TreeSitterSymbolExtractor(NullLogger<TreeSitterSymbolExtractor>.Instance);
        var path = Path.Combine(tempDir, $"{Guid.NewGuid()}{extension}");
        try
        {
            await File.WriteAllTextAsync(path, code);
            var result = await parser.ParseAsync(path);
            Assert.That(result, Is.Not.Null);
            var symbols = extractor.Extract(result!, path);
            return (symbols, result!, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptExtends_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class BaseClass {}
            class DerivedClass extends BaseClass {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "DerivedClass" &&
            r.TargetSymbolId == "BaseClass" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptImplements_CreatesImplements()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            interface MyInterface {}
            class MyClass implements MyInterface {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "MyClass" &&
            r.TargetSymbolId == "MyInterface" &&
            r.RelationshipType == "Implements"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptMethodCall_CreatesCalls()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Callee {
                targetMethod() {}
            }
            class Caller {
                callIt() {
                    new Callee().targetMethod();
                }
            }
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.RelationshipType == "Calls"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptTypeAnnotation_CreatesReferences()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class MyType {}
            class Consumer {
                ref: MyType;
            }
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.RelationshipType == "References" &&
            r.TargetSymbolId == "MyType"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_JavaExtends_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Parent {}
            class Child extends Parent {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".java");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "Child" &&
            r.TargetSymbolId == "Parent" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_NoDuplicateRelationships()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Base {}
            class Derived extends Base {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        var inheritsRels = relationships.Where(r => r.RelationshipType == "Inherits").ToList();
        Assert.That(inheritsRels, Has.Count.EqualTo(1));
    }
}
