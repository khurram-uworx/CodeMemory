using CodeMemory.Indexing.Git;
using CodeMemory.Services.Git;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CodeMemory.Tests.Services.Git;

public sealed class GitHistoryServiceTests : BaseServicesTests
{
    static string createTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryGitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        runGit("init", dir);
        runGit("config user.name testuser", dir);
        runGit("config user.email test@test.com", dir);

        File.WriteAllText(Path.Combine(dir, "test.cs"), "public class TestClass { }");
        runGit("add test.cs", dir);
        runGit("commit -m \"First commit\"", dir);

        File.WriteAllText(Path.Combine(dir, "test.cs"), "public class TestClass { public void Method() { } }");
        runGit("commit -am \"Second commit\"", dir);

        File.WriteAllText(Path.Combine(dir, "other.cs"), "public class Other { }");
        runGit("add other.cs", dir);
        runGit("commit -m \"Add other file\"", dir);

        return dir;
    }

    static void runGit(string args, string workDir)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }

    static string getLastCommitHash(string filePath, string repoRoot)
    {
        var psi = new ProcessStartInfo("git", $"--no-pager log -1 --format=\"%H\" -- \"{filePath}\"")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        return process.StandardOutput.ReadToEnd().Trim();
    }

    [Test]
    public async Task GetSymbolHistoryAsync_ReturnsNull_WhenSymbolNotFound()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "CodeMemoryTests");
        var dbPath = Path.Combine(repoRoot, Guid.NewGuid().ToString() + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var service = new GitHistoryService(NullLogger<GitHistoryService>.Instance, storage);
        var result = await service.GetSymbolHistoryAsync("NonExistent");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetSymbolHistoryAsync_ReturnsHistory_ForKnownSymbol()
    {
        var repoRoot = createTempRepo();
        var dbPath = Path.Combine(repoRoot, "test.db");
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var testClassGuid = Guid.NewGuid().ToString("N");
        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = testClassGuid,
                Name = "TestClass",
                Kind = "Class",
                FilePath = "test.cs",
                FullName = "TestClass",
                LineStart = 1, LineEnd = 1,
            }
        ]);

        var service = new GitHistoryService(NullLogger<GitHistoryService>.Instance, storage);
        var result = await service.GetSymbolHistoryAsync("TestClass");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.FilePath, Is.EqualTo("test.cs"));
        Assert.That(result.TotalCommits, Is.GreaterThanOrEqualTo(2));
        Assert.That(result.UniqueAuthors, Is.EqualTo(1));
    }

    [Test]
    public async Task GetHotspotsAsync_ReturnsOrderedResults()
    {
        var repoRoot = createTempRepo();
        var dbPath = Path.Combine(repoRoot, "test.db");
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var service = new GitHistoryService(NullLogger<GitHistoryService>.Instance, storage);
        var hotspots = await service.GetHotspotsAsync(5, 10);

        Assert.That(hotspots, Is.Not.Empty);
        Assert.That(hotspots.Count, Is.LessThanOrEqualTo(5));
        Assert.That(hotspots[0].CommitCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetSymbolHistoryAsync_RecentCommits_ArePopulated()
    {
        var repoRoot = createTempRepo();
        var dbPath = Path.Combine(repoRoot, "test.db");
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var testClassGuid = Guid.NewGuid().ToString("N");
        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = testClassGuid,
                Name = "TestClass",
                Kind = "Class",
                FilePath = "test.cs",
                FullName = "TestClass",
                LineStart = 1, LineEnd = 1,
            }
        ]);

        var service = new GitHistoryService(NullLogger<GitHistoryService>.Instance, storage);
        var result = await service.GetSymbolHistoryAsync("TestClass", 10);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.RecentCommits, Is.Not.Null);
        Assert.That(result.RecentCommits.Count, Is.GreaterThan(0));
        Assert.That(result.RecentCommits[0].Author, Is.EqualTo("testuser"));
        Assert.That(result.RecentCommits[0].Hash, Has.Length.EqualTo(40));
    }

    [Test]
    public async Task GetSymbolHistoryAsync_CacheHit_ReturnsCachedData()
    {
        var repoRoot = createTempRepo();
        var dbPath = Path.Combine(repoRoot, "test.db");
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var testClassGuid = Guid.NewGuid().ToString("N");
        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = testClassGuid,
                Name = "TestClass",
                Kind = "Class",
                FilePath = "test.cs",
                FullName = "TestClass",
                LineStart = 1, LineEnd = 1,
            }
        ]);

        var lastHash = getLastCommitHash("test.cs", repoRoot);

        // Pre-seed cache with FAKE data but REAL hash — should be served from cache
        var metricStore = new JsonGitMetricStore(storage, NullLogger<JsonGitMetricStore>.Instance);
        await metricStore.SetAsync("test.cs", new GitMetricEntry(
            "test.cs", 999, 99, "2099-01-01", "2099-01-01", lastHash));

        var service = new GitHistoryService(NullLogger<GitHistoryService>.Instance, storage, metricStore);
        var result = await service.GetSymbolHistoryAsync("TestClass");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalCommits, Is.EqualTo(999));
        Assert.That(result.UniqueAuthors, Is.EqualTo(99));
        Assert.That(result.LastCommitDate, Is.EqualTo("2099-01-01"));
    }

    [Test]
    public async Task GetSymbolHistoryAsync_CacheStale_Recomputes()
    {
        var repoRoot = createTempRepo();
        var dbPath = Path.Combine(repoRoot, "test.db");
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var testClassGuid = Guid.NewGuid().ToString("N");
        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = testClassGuid,
                Name = "TestClass",
                Kind = "Class",
                FilePath = "test.cs",
                FullName = "TestClass",
                LineStart = 1, LineEnd = 1,
            }
        ]);

        // Pre-seed cache with WRONG hash — should be rejected, recompute from git
        var metricStore = new JsonGitMetricStore(storage, NullLogger<JsonGitMetricStore>.Instance);
        await metricStore.SetAsync("test.cs", new GitMetricEntry(
            "test.cs", 999, 99, "2099-01-01", "2099-01-01", "nonexistenthash"));

        var service = new GitHistoryService(NullLogger<GitHistoryService>.Instance, storage, metricStore);
        var result = await service.GetSymbolHistoryAsync("TestClass");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalCommits, Is.EqualTo(2));
        Assert.That(result.UniqueAuthors, Is.EqualTo(1));
    }

    [Test]
    public async Task GetHotspotsAsync_SeedsMetricStore()
    {
        var repoRoot = createTempRepo();
        var dbPath = Path.Combine(repoRoot, "test.db");
        var storage = CreateStorage(repoRoot, dbPath);
        await storage.InitializeAsync();

        var metricStore = new JsonGitMetricStore(storage, NullLogger<JsonGitMetricStore>.Instance);
        var service = new GitHistoryService(NullLogger<GitHistoryService>.Instance, storage, metricStore);

        await service.GetHotspotsAsync(5, 10);

        var all = await metricStore.GetAllAsync();
        Assert.That(all, Is.Not.Empty);

        // The hotspot files should have been cached with a valid commit hash
        var testCs = all.Values.FirstOrDefault(e =>
            e.FilePath.EndsWith("test.cs", StringComparison.OrdinalIgnoreCase));
        Assert.That(testCs, Is.Not.Null);
        Assert.That(testCs.CommitCount, Is.GreaterThanOrEqualTo(2));
        Assert.That(testCs.LastCommitHash, Is.Not.Empty);
    }
}
