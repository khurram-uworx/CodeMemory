using CodeMemory.Indexing.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Extraction;

public sealed class RoslynRelationshipExtractorTests
{
    static readonly string fixturesDir = Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..",
        "CodeMemory.Tests", "fixtures");

    static SyntaxTree parseFixture(string fileName)
    {
        var path = Path.Combine(fixturesDir, fileName);
        var text = File.ReadAllText(path);
        return CSharpSyntaxTree.ParseText(text, path: path);
    }

    static IReadOnlyList<Symbol> extractSymbols(SyntaxTree tree, string filePath)
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        return extractor.Extract(tree, filePath);
    }

    [Test]
    public void ExtractRelationships_WithSampleRelationships_DetectsBaseTypeInheritance()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "MyDerivedClass" &&
            r.TargetSymbolId == "MyBaseClass" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public void ExtractRelationships_WithSampleRelationships_DetectsInterfaceImplementation()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "MyDerivedClass" &&
            r.TargetSymbolId == "IMyInterface" &&
            r.RelationshipType == "Implements"), Is.True);
    }

    [Test]
    public void ExtractRelationships_WithSampleRelationships_DetectsObjectCreationReferences()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "MyDerivedClass.DoSomething()" &&
            r.TargetSymbolId == "MyBaseClass" &&
            r.RelationshipType == "References"), Is.True);
    }

    [Test]
    public void ExtractRelationships_WithSampleRelationships_DetectsMethodCalls()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships.Any(r =>
            r.RelationshipType == "Calls"), Is.True);
    }

    [Test]
    public void ExtractRelationships_WithSampleRelationships_DetectsPropertyTypeReference()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "ReferenceHolder.Reference" &&
            r.TargetSymbolId == "MyBaseClass" &&
            r.RelationshipType == "References"), Is.True);
    }

    [Test]
    public void ExtractRelationships_WithSampleRelationships_DetectsParameterTypeReference()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId.Contains("Process") &&
            r.TargetSymbolId == "MyBaseClass" &&
            r.RelationshipType == "References"), Is.True);
    }

    [Test]
    public void ExtractRelationships_WithSampleClass_DoesNotCreateFalsePositivesForExternalTypes()
    {
        var tree = parseFixture("SampleClass.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships, Is.Empty);
    }

    [Test]
    public void ExtractRelationships_WithEmptyFile_ReturnsEmpty()
    {
        var tree = CSharpSyntaxTree.ParseText("// just a comment", path: "empty.cs");
        var symbols = new List<Symbol>();

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, "empty.cs");

        Assert.That(relationships, Is.Empty);
    }

    [Test]
    public void ExtractRelationships_AllRelationshipsHaveNonEmptyFields()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        foreach (var rel in relationships)
        {
            Assert.That(rel.SourceSymbolId, Is.Not.Empty);
            Assert.That(rel.TargetSymbolId, Is.Not.Empty);
            Assert.That(rel.RelationshipType, Is.Not.Empty);
        }
    }

    [Test]
    public void ExtractRelationships_WithSampleRelationships_ReturnsAtLeastSixRelationships()
    {
        var tree = parseFixture("SampleRelationships.cs");
        var filePath = tree.FilePath;
        var symbols = extractSymbols(tree, filePath);

        var extractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(tree, symbols, filePath);

        Assert.That(relationships.Count, Is.GreaterThanOrEqualTo(6));
    }
}
