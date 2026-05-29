using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using CodeMemory.Mcp.SqlQuery;
using CodeMemory.Storage;
using Memori.Embeddings;
using Memori.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CodeMemory.Tests.Services.Query;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class SqlQueryBenchmarks
{
    InMemoriVectorStore? store;
    SqlQueryService? service;
    string fullScanSql = "";

    [Params(1_000, 10_000, 100_000)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var store = new InMemoriVectorStore();
        var registry = new CollectionRegistry();
        var embeddingGenerator = new NgramEmbeddingGenerator();
        var logger = NullLogger<SqlQueryService>.Instance;
        var schemaProvider = new TableSchemaProvider();
        service = new SqlQueryService(registry, embeddingGenerator, logger, schemaProvider);
        this.store = store;

        var coll = store.GetCollection<string, SymbolRecord>("symbols");
        for (int i = 0; i < RowCount; i++)
        {
            var kind = (i % 3) switch { 0 => "Class", 1 => "Method", _ => "Field" };
            await coll.UpsertAsync(new SymbolRecord
            {
                Id = $"s:bench{i}",
                Name = $"Item{i:D6}",
                Kind = kind,
                FilePath = $"/src/module{i % 100}.cs",
                FullName = $"Namespace.Item{i}",
                LineStart = i % 100,
                LineEnd = (i % 100) + 10,
                Modifiers = i % 2 == 0 ? "public" : "internal"
            });
        }

        fullScanSql = $"SELECT * FROM SymbolRecord ORDER BY Name LIMIT {RowCount}";
    }

    [Benchmark]
    public async Task<SqlQueryResult> SimpleSelect() =>
        await service!.ExecuteAsync(store!, "SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 100");

    [Benchmark]
    public async Task<SqlQueryResult> GroupBy() =>
        await service!.ExecuteAsync(store!, "SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath ORDER BY cnt DESC");

    [Benchmark]
    public async Task<SqlQueryResult> FullScanOrderBy() =>
        await service!.ExecuteAsync(store!, fullScanSql);

    [Benchmark]
    public async Task<SqlQueryResult> Aggregate() =>
        await service!.ExecuteAsync(store!, "SELECT Kind, AVG(LineEnd - LineStart) AS avgLen FROM SymbolRecord GROUP BY Kind");
}

public sealed class SqlQueryServiceBenchmarkTests
{
    [Test]
    [Explicit("Run benchmarks manually to measure query latency at various row counts")]
    public void Run_BenchmarkBaselines() =>
        BenchmarkRunner.Run<SqlQueryBenchmarks>();

    [Test]
    [Explicit("Stress test — run manually, may use significant memory (~100 MB) and take 30-60 seconds")]
    public async Task StressTest_100kRecords_GroupBy_CompletesWithinTimeout()
    {
        var (store, registry, service) = SqlQueryServiceTests.createServices();
        var coll = store.GetCollection<string, SymbolRecord>("symbols");

        for (int i = 0; i < 100_000; i++)
        {
            var kind = (i % 5) switch
            {
                0 => "Class",
                1 => "Method",
                2 => "Field",
                3 => "Interface",
                _ => "Enum"
            };
            await coll.UpsertAsync(new SymbolRecord
            {
                Id = $"s:stress{i}",
                Name = $"Item{i:D6}",
                Kind = kind,
                FilePath = $"/src/module{i % 500}.cs",
                FullName = $"Namespace.Item{i}",
                LineStart = i % 100,
                LineEnd = (i % 100) + 10,
                Modifiers = i % 2 == 0 ? "public" : "internal"
            });
        }

        var sw = Stopwatch.StartNew();
        var result = await service.ExecuteAsync(store,
            "SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath ORDER BY cnt DESC");
        sw.Stop();

        Assert.That(result.Success, Is.True);
        Assert.That(result.RowCount, Is.AtMost(500));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(60)));
        TestContext.Out.WriteLine($"Stress test completed in {sw.Elapsed.TotalSeconds:F2}s, groups={result.RowCount}");
    }
}
