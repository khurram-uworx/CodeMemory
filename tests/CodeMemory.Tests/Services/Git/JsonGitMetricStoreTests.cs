using CodeMemory.Indexing.Git;
using CodeMemory.Services.Git;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeMemory.Tests.Services.Git;

public sealed class JsonGitMetricStoreTests
{
    static (JsonGitMetricStore Store, string RepoRoot) CreateStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryMetricTests", Guid.NewGuid().ToString());
        var storage = Substitute.For<IStorageService>();
        storage.RepoRoot.Returns(dir);
        var store = new JsonGitMetricStore(storage, NullLogger<JsonGitMetricStore>.Instance);
        return (store, dir);
    }

    static GitMetricEntry CreateEntry(string filePath, int commits = 5, int authors = 2,
        string hash = "abc123def456")
    {
        return new GitMetricEntry(
            filePath, commits, authors, "2026-06-01", "2025-01-15", hash,
            [
                new CommitInfo(hash, "dev1", "2026-06-01", "Latest"),
                new CommitInfo("def456abc", "dev2", "2025-12-01", "Earlier"),
            ]);
    }

    [Test]
    public async Task SetThenGet_ReturnsSameEntry()
    {
        var (store, _) = CreateStore();
        var entry = CreateEntry("src/Test.cs");

        await store.SetAsync("src/Test.cs", entry);
        var result = await store.GetAsync("src/Test.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.FilePath, Is.EqualTo("src/Test.cs"));
        Assert.That(result.CommitCount, Is.EqualTo(5));
        Assert.That(result.UniqueAuthors, Is.EqualTo(2));
        Assert.That(result.LastCommitHash, Is.EqualTo("abc123def456"));
        Assert.That(result.RecentCommits, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Get_NonExistent_ReturnsNull()
    {
        var (store, _) = CreateStore();
        var result = await store.GetAsync("src/NonExistent.cs");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Persistence_SurvivesInstanceRecreate()
    {
        var (store, repoRoot) = CreateStore();
        var entry = CreateEntry("src/Persist.cs");

        await store.SetAsync("src/Persist.cs", entry);
        store.Dispose();

        var storage2 = Substitute.For<IStorageService>();
        storage2.RepoRoot.Returns(repoRoot);
        var store2 = new JsonGitMetricStore(storage2, NullLogger<JsonGitMetricStore>.Instance);
        var result = await store2.GetAsync("src/Persist.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.CommitCount, Is.EqualTo(5));
        store2.Dispose();

        // Cleanup
        try { Directory.Delete(repoRoot, recursive: true); } catch { }
    }

    [Test]
    public async Task GetAll_ReturnsAllEntries()
    {
        var (store, _) = CreateStore();
        var entries = new Dictionary<string, GitMetricEntry>
        {
            ["src/A.cs"] = CreateEntry("src/A.cs", 1, 1, "aaa"),
            ["src/B.cs"] = CreateEntry("src/B.cs", 2, 1, "bbb"),
            ["src/C.cs"] = CreateEntry("src/C.cs", 3, 2, "ccc"),
        };

        await store.ReplaceAllAsync(entries);
        var all = await store.GetAllAsync();

        Assert.That(all, Has.Count.EqualTo(3));
        Assert.That(all["src/A.cs"].CommitCount, Is.EqualTo(1));
        Assert.That(all["src/B.cs"].CommitCount, Is.EqualTo(2));
        Assert.That(all["src/C.cs"].CommitCount, Is.EqualTo(3));
    }

    [Test]
    public async Task ReplaceAll_ReplacesAllEntries()
    {
        var (store, _) = CreateStore();
        var batchA = new Dictionary<string, GitMetricEntry>
        {
            ["src/A.cs"] = CreateEntry("src/A.cs", 1, 1, "aaa"),
            ["src/B.cs"] = CreateEntry("src/B.cs", 2, 1, "bbb"),
        };

        await store.ReplaceAllAsync(batchA);

        var batchB = new Dictionary<string, GitMetricEntry>
        {
            ["src/C.cs"] = CreateEntry("src/C.cs", 3, 2, "ccc"),
        };

        await store.ReplaceAllAsync(batchB);
        var all = await store.GetAllAsync();

        Assert.That(all, Has.Count.EqualTo(1));
        Assert.That(all.ContainsKey("src/C.cs"), Is.True);
    }

    [Test]
    public async Task UpsertBatch_MergesWithExisting()
    {
        var (store, _) = CreateStore();
        await store.SetAsync("src/A.cs", CreateEntry("src/A.cs", 1, 1, "aaa"));

        var upsert = new Dictionary<string, GitMetricEntry>
        {
            ["src/B.cs"] = CreateEntry("src/B.cs", 2, 1, "bbb"),
        };

        await store.UpsertBatchAsync(upsert);
        var all = await store.GetAllAsync();

        Assert.That(all, Has.Count.EqualTo(2));
        Assert.That(all["src/A.cs"].CommitCount, Is.EqualTo(1));
        Assert.That(all["src/B.cs"].CommitCount, Is.EqualTo(2));
    }

    [Test]
    public async Task CorruptedFile_ReturnsNull()
    {
        var (store, repoRoot) = CreateStore();
        var metricsPath = Path.Combine(repoRoot, ".codememory", "git-metrics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metricsPath)!);
        await File.WriteAllTextAsync(metricsPath, "not valid json");

        var result = await store.GetAsync("src/Any.cs");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task EmptyStore_GetAll_ReturnsEmpty()
    {
        var (store, _) = CreateStore();
        var all = await store.GetAllAsync();

        Assert.That(all, Is.Empty);
    }

    [Test]
    public async Task NormalizePath_HandlesBackslashes()
    {
        var (store, _) = CreateStore();
        var entry = CreateEntry("src\\Test.cs", 3, 1, "hash1");

        await store.SetAsync("src\\Test.cs", entry);
        var result = await store.GetAsync("src/Test.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.CommitCount, Is.EqualTo(3));
    }

    [Test]
    public async Task UpdateExisting_Overwrites()
    {
        var (store, _) = CreateStore();
        var entry1 = CreateEntry("src/Update.cs", 5, 1, "hash1");
        var entry2 = CreateEntry("src/Update.cs", 10, 3, "hash2");

        await store.SetAsync("src/Update.cs", entry1);
        await store.SetAsync("src/Update.cs", entry2);
        var result = await store.GetAsync("src/Update.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.CommitCount, Is.EqualTo(10));
        Assert.That(result.UniqueAuthors, Is.EqualTo(3));
        Assert.That(result.LastCommitHash, Is.EqualTo("hash2"));
    }

    [Test]
    public async Task NormalizePath_RelativizesAbsolutePath()
    {
        var (store, repoRoot) = CreateStore();
        var absPath = Path.Combine(repoRoot, "src", "AbsTest.cs");
        var entry = CreateEntry(absPath, 5, 2, "abchash");

        await store.SetAsync(absPath, entry);
        var result = await store.GetAsync("src/AbsTest.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.CommitCount, Is.EqualTo(5));
    }

    [Test]
    public async Task GetAsync_CaseInsensitiveLookup()
    {
        var (store, _) = CreateStore();
        var entry = CreateEntry("src/CaseTest.cs", 4, 1, "casehash");

        await store.SetAsync("src/CaseTest.cs", entry);
        var result = await store.GetAsync("SRC/casetest.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.CommitCount, Is.EqualTo(4));
    }
}
