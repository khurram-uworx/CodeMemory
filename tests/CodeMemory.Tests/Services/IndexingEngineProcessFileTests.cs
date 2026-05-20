using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Services;
using CodeMemory.Services.Architecture;
using CodeMemory.Storage;
using Memori.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services;

public sealed class IndexingEngineProcessFileTests
{
    static IndexingEngine CreateEngine(IStorageService storage)
    {
        var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance);
        var roslynParser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);
        var tsParser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var roslynExtractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var roslynRelExtractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
        var tsExtractor = new TreeSitterSymbolExtractor(NullLogger<TreeSitterSymbolExtractor>.Instance);
        var tsRelExtractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);
        var detector = new ProjectFileDetector(NullLogger<ProjectFileDetector>.Instance);

        return new IndexingEngine(
            NullLogger<IndexingEngine>.Instance, crawler,
            roslynParser, tsParser,
            roslynExtractor, roslynRelExtractor,
            tsExtractor, tsRelExtractor,
            chunker, storage, detector);
    }

    static IStorageService CreateInMemoryStorage(string repoRoot)
    {
        var store = new InMemoryVectorStore();
        return new StorageService(repoRoot, NullLogger<StorageService>.Instance, store);
    }

    [Test]
    public async Task ProcessFileAsync_OnCsFile_ReturnsSymbols()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryProcessFileTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "MyClass.cs");
        File.WriteAllText(filePath, """
            namespace Test;

            public class MyClass
            {
                public void DoSomething() { }
            }
            """);

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            var result = await engine.ProcessFileAsync(filePath, CancellationToken.None);

            Assert.That(result.FilePath, Is.EqualTo(filePath));
            Assert.That(result.Symbols, Is.Not.Empty);
            Assert.That(result.Symbols.Any(s => s.Name == "MyClass"), Is.True);
            Assert.That(result.Symbols.Any(s => s.FullName == "Test.MyClass.DoSomething()"), Is.True);
            Assert.That(result.Chunks, Is.Not.Empty);

            // All records reference the correct relative path
            foreach (var s in result.Symbols)
                Assert.That(s.FilePath, Is.EqualTo("MyClass.cs"));

            foreach (var c in result.Chunks)
                Assert.That(c.FilePath, Is.EqualTo("MyClass.cs"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ProcessFileAsync_OnCsFile_GuidIdsAreUnique()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryProcessFileTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "MyClass.cs");
        File.WriteAllText(filePath, """
            namespace Test;

            public class MyClass
            {
                public void DoSomething() { }
            }
            """);

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            var first = await engine.ProcessFileAsync(filePath, CancellationToken.None);
            var second = await engine.ProcessFileAsync(filePath, CancellationToken.None);

            // Each call generates fresh GUIDs
            var firstIds = first.Symbols.Select(s => s.Id).OrderBy(x => x).ToList();
            var secondIds = second.Symbols.Select(s => s.Id).OrderBy(x => x).ToList();
            Assert.That(firstIds, Is.Not.EquivalentTo(secondIds));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ProcessFileAsync_OnUnsupportedExtension_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryProcessFileTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "readme.txt");
        File.WriteAllText(filePath, "hello world");

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            var result = await engine.ProcessFileAsync(filePath, CancellationToken.None);

            Assert.That(result.FilePath, Is.EqualTo(filePath));
            Assert.That(result.Symbols, Is.Empty);
            Assert.That(result.Chunks, Is.Empty);
            Assert.That(result.Relationships, Is.Empty);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ProcessFileAsync_Html_ReturnsSymbols()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryProcessFileTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "index.html");
        File.WriteAllText(filePath, """<html><body><p>Hello</p></body></html>""");

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            var result = await engine.ProcessFileAsync(filePath, CancellationToken.None);

            // HTML produces no symbols but may produce a file-level chunk
            Assert.That(result.FilePath, Is.EqualTo(filePath));
            Assert.That(result.Symbols, Is.Empty);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
