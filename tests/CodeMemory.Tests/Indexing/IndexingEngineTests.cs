using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Services;
using CodeMemory.Storage;
using Memori.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing;

public sealed class IndexingEngineTests
{
    static string createTempDirWithFile(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryIndexingTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    [Test]
    public async Task Roslyn_EndToEnd_RelationshipsAreStored()
    {
        var source = """
            namespace Test;

            public interface IMyInterface
            {
                void DoSomething();
            }

            public class MyBaseClass
            {
                public void BaseMethod() { }
            }

            public class MyDerivedClass : MyBaseClass, IMyInterface
            {
                public void DoSomething()
                {
                    var obj = new MyBaseClass();
                    obj.BaseMethod();
                }
            }

            public class ReferenceHolder
            {
                public MyBaseClass? Reference { get; set; }

                public void Process(MyBaseClass input)
                {
                    var local = new MyDerivedClass();
                    local.DoSomething();
                }
            }
            """;

        var repoDir = createTempDirWithFile("test.cs", source);
        try
        {
            var store = new InMemoryVectorStore();
            var storage = new StorageService(repoDir,
                NullLogger<StorageService>.Instance, store);

            var crawler = new FileCrawler(NullLogger<FileCrawler>.Instance);
            var roslynParser = new RoslynCSharpParser(NullLogger<RoslynCSharpParser>.Instance);
            var tsParser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
            var roslynExtractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
            var roslynRelExtractor = new RoslynRelationshipExtractor(NullLogger<RoslynRelationshipExtractor>.Instance);
            var tsExtractor = new TreeSitterSymbolExtractor(NullLogger<TreeSitterSymbolExtractor>.Instance);
            var tsRelExtractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
            var chunker = new SemanticChunker(NullLogger<SemanticChunker>.Instance);

            var engine = new IndexingEngine(
                NullLogger<IndexingEngine>.Instance, crawler,
                roslynParser, tsParser,
                roslynExtractor, roslynRelExtractor,
                tsExtractor, tsRelExtractor,
                chunker, storage);

            await engine.RunIndexingAsync(repoDir, default);

            var allSymbols = await storage.GetSymbolsByKindAsync("Class", 100);
            Assert.That(allSymbols, Is.Not.Empty);
            Assert.That(allSymbols.Any(s => s.FullName == "Test.MyBaseClass"), Is.True);
            Assert.That(allSymbols.Any(s => s.FullName == "Test.MyDerivedClass"), Is.True);
            Assert.That(allSymbols.Any(s => s.FullName == "Test.ReferenceHolder"), Is.True);

            // Verify relationships were stored
            var myDerivedClass = allSymbols.First(s => s.FullName == "Test.MyDerivedClass");
            var deps = await storage.GetRelationshipsBySourceAsync(myDerivedClass.Id);
            Assert.That(deps, Is.Not.Empty, "MyDerivedClass should have outgoing relationships");

            var inherits = deps.Where(r => r.RelationshipType == "Inherits").ToList();
            Assert.That(inherits, Has.Count.EqualTo(1),
                "MyDerivedClass should inherit from MyBaseClass");
            var myBaseClass = allSymbols.First(s => s.FullName == "Test.MyBaseClass");
            Assert.That(inherits[0].TargetSymbolId, Is.EqualTo(myBaseClass.Id));

            var implements = deps.Where(r => r.RelationshipType == "Implements").ToList();
            Assert.That(implements, Has.Count.EqualTo(1),
                "MyDerivedClass should implement IMyInterface");

            // IMyInterface is an Interface kind, look it up directly
            var myInterface = await storage.GetSymbolByFullNameAsync("Test.IMyInterface");
            Assert.That(myInterface, Is.Not.Null);
            Assert.That(implements[0].TargetSymbolId, Is.EqualTo(myInterface.Id));
        }
        finally
        {
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }
}
