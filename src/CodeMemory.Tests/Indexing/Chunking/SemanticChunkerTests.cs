using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Chunking;

public sealed class SemanticChunkerTests
{
    static readonly string fixturesDir = Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..",
        "CodeMemory.Tests", "fixtures");

    static (IReadOnlyList<Symbol> Symbols, string FileText) extractSymbols(string fileName)
    {
        var path = Path.Combine(fixturesDir, fileName);
        var text = File.ReadAllText(path);
        var syntaxTree = CSharpSyntaxTree.ParseText(text, path: path);

        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var symbols = extractor.Extract(syntaxTree, path);

        return (symbols, text);
    }

    [Test]
    public void ChunkAll_WithSampleClass_ProducesExpectedChunkCount()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        // 11 types + 16 members = 27 chunks
        Assert.That(chunks, Has.Count.EqualTo(27));
    }

    [Test]
    public void ChunkAll_WithSampleClass_TypeChunksIncludeFileContext()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var sampleClassChunk = chunks.First(c => c.SymbolId == "SampleClass");
        Assert.That(sampleClassChunk.Content, Does.Contain("using System.Numerics;"));
        Assert.That(sampleClassChunk.Content, Does.Contain("using System.Text.RegularExpressions;"));
        Assert.That(sampleClassChunk.Content, Does.Contain("namespace CodeMemory.Tests.Fixtures;"));
        Assert.That(sampleClassChunk.Content, Does.Contain("public class SampleClass"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_MemberChunksIncludeParentContext()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var addMethod = chunks.First(c => c.SymbolId == "SampleClass.Add(int a, int b)");
        Assert.That(addMethod.Content, Does.Contain("// Parent: SampleClass"));
        Assert.That(addMethod.Content, Does.Contain("public int Add(int a, int b)"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_MethodChunkContainsBody()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var doNothing = chunks.First(c => c.SymbolId == "SampleClass.DoNothing()");
        Assert.That(doNothing.Content, Does.Contain("// intentional no-op"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_PropertyChunkContainsGetter()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var nameProp = chunks.First(c => c.SymbolId == "SampleClass.Name");
        Assert.That(nameProp.Content, Does.Contain("{ get; set; }"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_NestedTypeChunkHasCorrectSymbolId()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var innerClass = chunks.First(c => c.SymbolId == "OuterClass.InnerClass");
        Assert.That(innerClass, Is.Not.Null);
        Assert.That(innerClass.Content, Does.Contain("public class InnerClass"));
        Assert.That(innerClass.Metadata["chunkType"], Is.EqualTo("type"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_NestedMemberChunkHasParentChain()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var innerMethod = chunks.First(c => c.SymbolId == "OuterClass.InnerClass.InnerMethod()");
        Assert.That(innerMethod, Is.Not.Null);
        Assert.That(innerMethod.Content, Does.Contain("// Parent: OuterClass.InnerClass"));
        Assert.That(innerMethod.Content, Does.Contain("public void InnerMethod() { }"));
        Assert.That(innerMethod.Metadata["parentName"], Is.EqualTo("OuterClass.InnerClass"));
    }

    [Test]
    public void ChunkAll_IsDeterministic()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks1 = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);
        var chunks2 = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        Assert.That(chunks1.Select(c => c.Id), Is.EqualTo(chunks2.Select(c => c.Id)));
        Assert.That(chunks1.Select(c => c.Content), Is.EqualTo(chunks2.Select(c => c.Content)));
    }

    [Test]
    public void ChunkAll_WithRecord_ProducesTypeChunk()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var person = chunks.FirstOrDefault(c => c.SymbolId == "Person");
        Assert.That(person, Is.Not.Null);
        Assert.That(person.Content, Does.Contain("public record Person"));
    }

    [Test]
    public void ChunkAll_WithEnum_ProducesTypeChunk()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var status = chunks.FirstOrDefault(c => c.SymbolId == "Status");
        Assert.That(status, Is.Not.Null);
        Assert.That(status.Content, Does.Contain("public enum Status"));
    }

    [Test]
    public void ChunkAll_WithEmptySymbols_ReturnsEmpty()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var symbols = Array.Empty<Symbol>();

        var chunks = chunker.ChunkAll(symbols, "", "empty.cs", Language.Unknown);

        Assert.That(chunks, Is.Empty);
    }

    [Test]
    public void ChunkAll_AllChunksHaveRequiredFields()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        foreach (var chunk in chunks)
        {
            Assert.That(chunk.Id, Is.Not.Null.And.Not.Empty, $"Chunk {chunk.SymbolId} missing Id");
            Assert.That(chunk.SymbolId, Is.Not.Null.And.Not.Empty);
            Assert.That(chunk.FilePath, Is.Not.Null.And.Not.Empty);
            Assert.That(chunk.Content, Is.Not.Null.And.Not.Empty);
            Assert.That(chunk.Language, Is.EqualTo("CSharp"));
            Assert.That(chunk.Metadata, Is.Not.Null);
            Assert.That(chunk.Metadata, Contains.Key("chunkType"));
            Assert.That(chunk.Metadata, Contains.Key("symbolKind"));
        }
    }
}
