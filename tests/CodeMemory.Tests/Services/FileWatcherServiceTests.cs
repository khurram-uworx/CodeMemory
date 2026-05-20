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

public sealed class FileWatcherServiceTests
{
    const int PollIntervalMs = 200;
    const int TimeoutMs = 15_000;

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

    static async Task<IReadOnlyList<SymbolRecord>> PollSymbolsAsync(
        IStorageService storage, string filePath, int? minCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var symbols = await storage.GetSymbolsByFileAsync(filePath, 1000);

            if (minCount.HasValue)
            {
                if (symbols.Count >= minCount.Value)
                    return symbols;
            }
            else if (symbols.Count == 0)
            {
                return symbols;
            }

            await Task.Delay(PollIntervalMs);
        }

        var final = await storage.GetSymbolsByFileAsync(filePath, 1000);
        return final;
    }

    [Test]
    public async Task StartWatcher_CreateFile_SymbolsAppearInStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryWatcherTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            using var watcher = new FileWatcherService(
                dir, storage, engine, NullLogger<FileWatcherService>.Instance);

            await watcher.StartAsync(CancellationToken.None);

            var filePath = Path.Combine(dir, "MyClass.cs");
            File.WriteAllText(filePath, """
                namespace Test;
                public class MyClass
                {
                    public void Helper() { }
                }
                """);

            var symbols = await PollSymbolsAsync(storage, "MyClass.cs",
                minCount: 1, TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.That(symbols, Is.Not.Empty, "Symbols should appear after watcher processes the file");
            Assert.That(symbols.Any(s => s.Name == "MyClass"), Is.True);
            Assert.That(symbols.Any(s => s.FullName == "Test.MyClass.Helper()"), Is.True);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task StartWatcher_ModifyFile_OldSymbolsGoneNewSymbolsPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryWatcherTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            using var watcher = new FileWatcherService(
                dir, storage, engine, NullLogger<FileWatcherService>.Instance);

            await watcher.StartAsync(CancellationToken.None);

            // Write initial file with Foo class
            var filePath = Path.Combine(dir, "MyClass.cs");
            File.WriteAllText(filePath, """
                namespace Test;
                public class Foo
                {
                    public void DoFoo() { }
                }
                """);

            // Wait for initial indexing
            var symbols = await PollSymbolsAsync(storage, "MyClass.cs",
                minCount: 1, TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.That(symbols.Any(s => s.Name == "Foo"), Is.True,
                "Initial file should be indexed with Foo");

            // Capture old symbol IDs
            var oldIds = symbols.Select(s => s.Id).ToHashSet();

            // Overwrite with Bar class
            File.WriteAllText(filePath, """
                namespace Test;
                public class Bar
                {
                    public void DoBar() { }
                }
                """);

            // Wait for Bar to appear (poll by specific name since initial Foo symbols still exist briefly)
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(TimeoutMs);
            IReadOnlyList<SymbolRecord>? newSymbols = null;

            while (DateTime.UtcNow < deadline)
            {
                var current = await storage.GetSymbolsByFileAsync("MyClass.cs", 1000);
                if (current.Any(s => s.Name == "Bar"))
                {
                    newSymbols = current;
                    break;
                }
                await Task.Delay(PollIntervalMs);
            }

            Assert.That(newSymbols, Is.Not.Null, "Re-indexed file should contain Bar");
            Assert.That(newSymbols.Any(s => s.Name == "Bar"), Is.True);

            // Verify old symbols are gone (no old IDs remain)
            var newIds = newSymbols.Select(s => s.Id).ToHashSet();
            Assert.That(oldIds.Intersect(newIds), Is.Empty,
                "Old symbol IDs should be deleted and replaced with new ones");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task StartWatcher_DeleteFile_RecordsRemoved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryWatcherTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            using var watcher = new FileWatcherService(
                dir, storage, engine, NullLogger<FileWatcherService>.Instance);

            await watcher.StartAsync(CancellationToken.None);

            // Create a file and wait for indexing
            var filePath = Path.Combine(dir, "MyClass.cs");
            File.WriteAllText(filePath, """
                namespace Test;
                public class MyClass { }
                """);

            var symbols = await PollSymbolsAsync(storage, "MyClass.cs",
                minCount: 1, TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.That(symbols, Is.Not.Empty, "File should be indexed before deletion");

            // Delete the file
            File.Delete(filePath);

            // Wait for deletion to propagate
            var remaining = await PollSymbolsAsync(storage, "MyClass.cs",
                minCount: null, TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.That(remaining, Is.Empty,
                "All symbols should be removed after file deletion");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task StartWatcher_UnsupportedExtension_Ignored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryWatcherTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            var storage = CreateInMemoryStorage(dir);
            await storage.InitializeAsync();

            var engine = CreateEngine(storage);
            using var watcher = new FileWatcherService(
                dir, storage, engine, NullLogger<FileWatcherService>.Instance);

            await watcher.StartAsync(CancellationToken.None);

            var filePath = Path.Combine(dir, "readme.txt");
            File.WriteAllText(filePath, "hello world");

            // Give the watcher time to potentially fire (it shouldn't)
            await Task.Delay(2500);

            var symbols = await storage.GetSymbolsByFileAsync("readme.txt", 100);
            Assert.That(symbols, Is.Empty, ".txt files should be ignored by the watcher");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    static IStorageService CreateInMemoryStorage(string repoRoot)
    {
        var store = new InMemoryVectorStore();
        return new StorageService(repoRoot, NullLogger<StorageService>.Instance, store);
    }

    [TearDown]
    public void TearDown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryWatcherTests");
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
