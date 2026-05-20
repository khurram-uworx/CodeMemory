using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Chunking;

public sealed class SemanticChunkerTests
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

    static async Task<(IReadOnlyList<Symbol> Symbols, string FileText)> extractTsSymbols(
        string code, string extension)
    {
        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var extractor = new TreeSitterSymbolExtractor(NullLogger<TreeSitterSymbolExtractor>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{extension}");
        try
        {
            await File.WriteAllTextAsync(path, code);
            var result = await parser.ParseAsync(path);
            Assert.That(result, Is.Not.Null);
            var symbols = extractor.Extract(result!, path);
            return (symbols, result!.FileText);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
        var sampleClassChunk = chunks.First(c => c.SymbolId == "CodeMemory.Tests.Fixtures.SampleClass");

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

        var addMethod = chunks.First(c => c.SymbolId == "CodeMemory.Tests.Fixtures.SampleClass.Add(int a, int b)");
        Assert.That(addMethod.Content, Does.Contain("// Parent: CodeMemory.Tests.Fixtures.SampleClass"));
        Assert.That(addMethod.Content, Does.Contain("public int Add(int a, int b)"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_MethodChunkContainsBody()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var doNothing = chunks.First(c => c.SymbolId == "CodeMemory.Tests.Fixtures.SampleClass.DoNothing()");
        Assert.That(doNothing.Content, Does.Contain("// intentional no-op"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_PropertyChunkContainsGetter()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var nameProp = chunks.First(c => c.SymbolId == "CodeMemory.Tests.Fixtures.SampleClass.Name");
        Assert.That(nameProp.Content, Does.Contain("{ get; set; }"));
    }

    [Test]
    public void ChunkAll_WithSampleClass_NestedTypeChunkHasCorrectSymbolId()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var innerClass = chunks.First(c => c.SymbolId == "CodeMemory.Tests.Fixtures.OuterClass.InnerClass");
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

        var innerMethod = chunks.First(c => c.SymbolId == "CodeMemory.Tests.Fixtures.OuterClass.InnerClass.InnerMethod()");
        Assert.That(innerMethod, Is.Not.Null);
        Assert.That(innerMethod.Content, Does.Contain("// Parent: CodeMemory.Tests.Fixtures.OuterClass.InnerClass"));
        Assert.That(innerMethod.Metadata["parentName"], Is.EqualTo("CodeMemory.Tests.Fixtures.OuterClass.InnerClass"));
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

        var person = chunks.FirstOrDefault(c => c.SymbolId == "CodeMemory.Tests.Fixtures.Person");
        Assert.That(person, Is.Not.Null);
        Assert.That(person.Content, Does.Contain("public record Person"));
    }

    [Test]
    public void ChunkAll_WithEnum_ProducesTypeChunk()
    {
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var (symbols, fileText) = extractSymbols("SampleClass.cs");

        var chunks = chunker.ChunkAll(symbols, fileText, "SampleClass.cs", Language.CSharp);

        var status = chunks.FirstOrDefault(c => c.SymbolId == "CodeMemory.Tests.Fixtures.Status");
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

    [Test]
    public async Task ChunkAll_WithTypeScript_ImportInFileContext()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            import { Component } from './component';
            import { Helper } from './helper';

            export class MyService {
                doWork() {}
            }
            """;

        var (symbols, fileText) = await extractTsSymbols(code, ".ts");
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var chunks = chunker.ChunkAll(symbols, fileText, "test.ts", Language.TypeScript);

        var serviceChunk = chunks.FirstOrDefault(c => c.SymbolId == "MyService");
        Assert.That(serviceChunk, Is.Not.Null);
        Assert.That(serviceChunk!.Content, Does.Contain("import { Component } from './component'"));
        Assert.That(serviceChunk.Content, Does.Contain("import { Helper } from './helper'"));
    }

    [Test]
    public async Task ChunkAll_WithJava_ImportAndPackageInFileContext()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            package com.example;
            import java.util.List;
            import java.util.ArrayList;

            public class MyService {
                public void execute() {}
            }
            """;

        var (symbols, fileText) = await extractTsSymbols(code, ".java");
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var chunks = chunker.ChunkAll(symbols, fileText, "test.java", Language.Java);

        var serviceChunk = chunks.FirstOrDefault(c => c.SymbolId == "MyService");
        Assert.That(serviceChunk, Is.Not.Null);
        Assert.That(serviceChunk!.Content, Does.Contain("package com.example;"));
        Assert.That(serviceChunk.Content, Does.Contain("import java.util.List;"));
        Assert.That(serviceChunk.Content, Does.Contain("import java.util.ArrayList;"));
    }

    [Test]
    public async Task ChunkAll_WithGo_ImportAndPackageInFileContext()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            package main
            import "fmt"
            import "os"

            type Worker struct {
                Name string
            }
            func (w Worker) Work() {}
            """;

        var (symbols, fileText) = await extractTsSymbols(code, ".go");
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var chunks = chunker.ChunkAll(symbols, fileText, "test.go", Language.Go);

        var workerChunk = chunks.FirstOrDefault(c => c.SymbolId == "Worker");
        Assert.That(workerChunk, Is.Not.Null);
        Assert.That(workerChunk!.Content, Does.Contain("package main"));
        Assert.That(workerChunk.Content, Does.Contain("import \"fmt\""));
        Assert.That(workerChunk.Content, Does.Contain("import \"os\""));
    }

    [Test]
    public async Task ChunkAll_WithRust_UseInFileContext()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            use std::collections::HashMap;
            use std::io;

            struct Config {
                values: HashMap<String, String>,
            }
            fn run() {}
            """;

        var (symbols, fileText) = await extractTsSymbols(code, ".rs");
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var chunks = chunker.ChunkAll(symbols, fileText, "test.rs", Language.Rust);

        var configChunk = chunks.FirstOrDefault(c => c.SymbolId == "Config");
        Assert.That(configChunk, Is.Not.Null);
        Assert.That(configChunk!.Content, Does.Contain("use std::collections::HashMap;"));
        Assert.That(configChunk.Content, Does.Contain("use std::io;"));
    }
}
